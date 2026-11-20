using Azure.Core;
using Azure.Identity;
using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;
using DocDetectives.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocDetectives.Infrastructure.Foundry;

/// <summary>
/// Sends PDF, DOCX, and Image manuals to Azure AI Content Understanding (Microsoft Foundry)
/// using the :analyzeBinary endpoint and polls until Markdown and structured fields are ready.
/// A file whose bytes were already analyzed is read back from the local cache instead of being uploaded again.
/// </summary>
public sealed class AzureContentUnderstandingExtractor(
    HttpClient httpClient,
    IOptions<DocDetectivesOptions> options) : IManualExtractor
{
    private readonly ContentUnderstandingOptions _config = options.Value.FoundryContentUnderstanding;
    private static readonly TokenCredential Credential = new DefaultAzureCredential();

    public async Task<ExtractedManual> ExtractAsync(
        Stream fileStream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        using var memoryStream = new MemoryStream();
        await fileStream.CopyToAsync(memoryStream, cancellationToken);
        var fileBytes = memoryStream.ToArray();

        var (manualId, contentHash) = Identify(fileName, fileBytes);
        var cachePath = CachePathFor(contentHash);

        if (cachePath is not null && File.Exists(cachePath))
        {
            try
            {
                var cachedJson = await File.ReadAllTextAsync(cachePath, cancellationToken);
                if (!string.IsNullOrWhiteSpace(cachedJson))
                {
                    return ParseExtractedManual(manualId, fileName, cachedJson);
                }
            }
            catch (JsonException)
            {
                // A half-written cache file is ignored; the document is read from Content Understanding again.
            }
            catch (IOException)
            {
                // Cache is a shortcut. If it cannot be read, fall through to Content Understanding.
            }
        }

        var endpoint = _config.Endpoint.TrimEnd('/');
        var analyzeUrl =
            $"{endpoint}/contentunderstanding/analyzers/{_config.AnalyzerId}:analyzeBinary?api-version={_config.ApiVersion}";

        using var analyzeRequest = new HttpRequestMessage(HttpMethod.Post, analyzeUrl)
        {
            Content = new ByteArrayContent(fileBytes)
        };
        analyzeRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(fileName));
        await AttachAuthHeaderAsync(analyzeRequest, cancellationToken);

        using var initialResponse = await httpClient.SendAsync(analyzeRequest, cancellationToken);
        if (!initialResponse.IsSuccessStatusCode)
        {
            var errorBody = await initialResponse.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Azure AI Content Understanding analyze request failed ({(int)initialResponse.StatusCode}): {errorBody}");
        }

        if (!initialResponse.Headers.TryGetValues("Operation-Location", out var locations))
        {
            throw new InvalidOperationException("Missing Operation-Location header from Content Understanding API.");
        }

        var operationUrl = locations.First();
        var rawJson = await PollForResultAsync(operationUrl, cancellationToken);
        await TrySaveCacheAsync(cachePath, rawJson, cancellationToken);

        return ParseExtractedManual(manualId, fileName, rawJson);
    }

    private string? CachePathFor(string contentHash)
    {
        if (string.IsNullOrWhiteSpace(_config.CacheFolder))
        {
            return null;
        }

        return Path.Combine(_config.CacheFolder, contentHash + ".json");
    }

    private static async Task TrySaveCacheAsync(string? cachePath, string rawJson, CancellationToken cancellationToken)
    {
        if (cachePath is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            // Unique temporary name so two documents that share bytes can finish saving at the same time.
            var temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, rawJson, cancellationToken);
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        catch (IOException)
        {
            // The read already succeeded. A cache write failure must not fail the document.
        }
    }

    private async Task<string> PollForResultAsync(string operationUrl, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(1, _config.PollIntervalSeconds));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var pollRequest = new HttpRequestMessage(HttpMethod.Get, operationUrl);
            await AttachAuthHeaderAsync(pollRequest, cancellationToken);

            using var pollResponse = await httpClient.SendAsync(pollRequest, cancellationToken);
            var json = await pollResponse.Content.ReadAsStringAsync(cancellationToken);

            if (!pollResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Polling Content Understanding failed ({(int)pollResponse.StatusCode}): {json}");
            }

            using var doc = JsonDocument.Parse(json);
            var status = doc.RootElement.TryGetProperty("status", out var statusProp)
                ? statusProp.GetString()
                : null;

            if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                return json;
            }

            if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Content Understanding analysis failed: {json}");
            }

            await Task.Delay(delay, cancellationToken);
        }
    }

    private async Task AttachAuthHeaderAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var hasApiKey = !string.IsNullOrWhiteSpace(_config.ApiKey)
                        && !_config.ApiKey.StartsWith("<YOUR_", StringComparison.OrdinalIgnoreCase);

        if (hasApiKey)
        {
            request.Headers.Add("Ocp-Apim-Subscription-Key", _config.ApiKey);
            return;
        }

        var token = await Credential.GetTokenAsync(
            new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]),
            cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private static string GetMimeType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".doc" => "application/msword",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".tiff" or ".tif" => "image/tiff",
            _ => "application/octet-stream"
        };

    private static ExtractedManual ParseExtractedManual(string manualId, string fileName, string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var markdownBuilder = new StringBuilder();
        string? manufacturer = null;
        string? category = null;
        var models = new List<string>();

        if (doc.RootElement.TryGetProperty("result", out var result) &&
            result.TryGetProperty("contents", out var contents) &&
            contents.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in contents.EnumerateArray())
            {
                if (item.TryGetProperty("markdown", out var mdProp))
                {
                    markdownBuilder.AppendLine(mdProp.GetString());
                }

                if (item.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
                {
                    manufacturer ??= ReadStringField(fields, "Manufacturer");
                    category ??= ReadStringField(fields, "EquipmentCategory");

                    if (fields.TryGetProperty("ModelNumbers", out var modelArray) &&
                        modelArray.TryGetProperty("valueArray", out var values) &&
                        values.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in values.EnumerateArray())
                        {
                            if (entry.TryGetProperty("valueString", out var modelVal) &&
                                !string.IsNullOrWhiteSpace(modelVal.GetString()))
                            {
                                models.Add(modelVal.GetString()!);
                            }
                        }
                    }
                }
            }
        }

        return new ExtractedManual(
            ManualId: manualId,
            FileName: fileName,
            MarkdownContent: markdownBuilder.ToString().Trim(),
            Manufacturer: manufacturer,
            ModelNumbers: models,
            EquipmentCategory: category,
            RawJsonResponse: rawJson);
    }

    private static string? ReadStringField(JsonElement fields, string fieldName)
    {
        if (fields.TryGetProperty(fieldName, out var fieldObj) &&
            fieldObj.TryGetProperty("valueString", out var valueProp))
        {
            return valueProp.GetString();
        }

        return null;
    }

    // The manual id keeps its existing shape: sanitized file name plus the first 12 hex chars of the content hash.
    // The cache file uses the full hash so two different files cannot share a saved response.
    private static (string ManualId, string ContentHash) Identify(string fileName, byte[] content)
    {
        var contentHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var sanitizedName = Regex.Replace(
            Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant(),
            "[^a-z0-9_-]",
            "_");
        return ($"{sanitizedName}_{contentHash[..12]}", contentHash);
    }
}