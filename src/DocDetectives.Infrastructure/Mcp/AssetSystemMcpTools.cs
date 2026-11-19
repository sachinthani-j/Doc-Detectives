using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace DocDetectives.Infrastructure.Mcp;

/// <summary>
/// Exposes existing system asset lookup and manual-linking operations as standardized MCP Tools.
/// </summary>
[McpServerToolType]
public sealed class AssetSystemMcpTools(IAssetCatalogGateway assetCatalog)
{
    [McpServerTool(Name = "search_assets")]
    [Description("Search existing equipment assets in the system by manufacturer or model keyword.")]
    public async Task<string> SearchAssets(
        [Description("Equipment manufacturer name, e.g., Grundfos or Atlas Copco")] string? manufacturer = null,
        [Description("Model number or equipment keyword")] string? modelKeyword = null,
        CancellationToken cancellationToken = default)
    {
        var assets = await assetCatalog.SearchAssetsAsync(manufacturer, modelKeyword, cancellationToken);
        return JsonSerializer.Serialize(assets);
    }

    [McpServerTool(Name = "link_manual_to_asset")]
    [Description("Persist a mapping between an ingested technical manual and a system asset.")]
    public async Task<string> LinkManualToAsset(
        [Description("Target system Asset ID")] string assetId,
        [Description("Ingested Manual ID")] string manualId,
        [Description("Match confidence score between 0.0 and 1.0")] double confidenceScore,
        CancellationToken cancellationToken = default)
    {
        await assetCatalog.LinkManualToAssetAsync(
            assetId,
            manualId,
            confidenceScore,
            MappingStatus.AutoLinked,
            cancellationToken);

        return $"Linked manual '{manualId}' to asset '{assetId}' (confidence: {confidenceScore:P0}).";
    }

    [McpServerTool(Name = "get_asset_details")]
    [Description("Retrieve full equipment specifications for a specific Asset ID.")]
    public async Task<string> GetAssetDetails(
        [Description("Target system Asset ID")] string assetId,
        CancellationToken cancellationToken = default)
    {
        var asset = await assetCatalog.GetAssetByIdAsync(assetId, cancellationToken);
        return asset is null
            ? $"Asset '{assetId}' not found."
            : JsonSerializer.Serialize(asset);
    }
}