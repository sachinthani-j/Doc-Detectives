using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;
using Microsoft.Extensions.AI;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DocDetectives.Tests.Corpus;

/// <summary>Asset catalog backed by the synthetic asset export. Search: manufacturer contains, any model token matches.</summary>
public sealed class CorpusCatalog(IReadOnlyList<Asset> assets) : IAssetCatalogGateway
{
    public List<(string AssetId, string ManualId, double Score, MappingStatus Status)> Links { get; } = [];

    public Task<IReadOnlyList<Asset>> SearchAssetsAsync(string? manufacturer, string? modelKeyword, CancellationToken cancellationToken = default)
    {
        IEnumerable<Asset> q = assets;
        if (!string.IsNullOrWhiteSpace(manufacturer))
            q = q.Where(a => a.Manufacturer.Contains(manufacturer, StringComparison.OrdinalIgnoreCase));
        var tokens = (modelKeyword ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 0)
            q = q.Where(a => tokens.Any(t =>
                a.ModelNumber.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                a.Name.Contains(t, StringComparison.OrdinalIgnoreCase)));
        return Task.FromResult<IReadOnlyList<Asset>>(q.ToList());
    }

    public Task LinkManualToAssetAsync(string assetId, string manualId, double confidenceScore, MappingStatus status, CancellationToken cancellationToken = default)
    {
        Links.Add((assetId, manualId, confidenceScore, status));
        return Task.CompletedTask;
    }

    public Task<Asset?> GetAssetByIdAsync(string assetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Asset?> FindAssetBySerialNumberAsync(string serialNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<Asset>> GetChildAssetsAsync(string parentAssetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CreateAssetsAsync(IReadOnlyList<Asset> assets, string manualId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>
/// Deterministic stand-in for the LLM. It reads the candidate lines the skill puts in the prompt and scores them:
/// manufacturer + exact model 0.95; exact model only 0.70; same manufacturer and category but other model 0.55; else omitted.
/// With a fixed score it returns that score for every candidate instead (0 omits the candidate).
/// </summary>
public sealed class ScoringChat(double? fixedScore = null) : IChatClient
{
    public int Calls { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        var prompt = string.Join("\n", messages.Select(m => m.Text));
        var manufacturer = Field(prompt, "- Manufacturer: ");
        var models = Field(prompt, "- Model Numbers: ").Split(", ", StringSplitOptions.RemoveEmptyEntries);
        var category = Field(prompt, "- Equipment Category: ");

        var items = new List<string>();
        foreach (Match m in Regex.Matches(prompt, @"^- AssetId: (?<id>.+?) \| Name: .+? \| Manufacturer: (?<mf>.*?) \| Model: (?<model>.*?) \| Category: (?<cat>.*)$", RegexOptions.Multiline))
        {
            var sameMf = m.Groups["mf"].Value.Equals(manufacturer, StringComparison.OrdinalIgnoreCase);
            var sameModel = models.Any(x => x.Equals(m.Groups["model"].Value, StringComparison.OrdinalIgnoreCase));
            var sameCat = m.Groups["cat"].Value.Trim().Equals(category, StringComparison.OrdinalIgnoreCase);
            double score = fixedScore ?? (sameMf && sameModel ? 0.95 : sameModel ? 0.70 : sameMf && sameCat ? 0.55 : 0.0);
            if (score > 0)
                items.Add("{\"assetId\":\"" + m.Groups["id"].Value + "\",\"confidenceScore\":" +
                          score.ToString(CultureInfo.InvariantCulture) + ",\"reasoning\":\"scripted\"}");
        }
        var json = "{\"matches\":[" + string.Join(",", items) + "]}";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
    }

    private static string Field(string prompt, string label)
    {
        var i = prompt.IndexOf(label, StringComparison.Ordinal);
        if (i < 0) return "";
        var s = i + label.Length;
        var e = prompt.IndexOf('\n', s);
        return (e < 0 ? prompt[s..] : prompt[s..e]).Trim();
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
