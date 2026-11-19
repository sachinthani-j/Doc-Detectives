using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;
using System.Collections.Concurrent;

namespace DocDetectives.Infrastructure.Persistence;

/// <summary>
/// Seeded asset store adapter. Replace with your ERP/EAM SQL or REST repository in production.
/// </summary>
public sealed class InMemoryAssetCatalogGateway : IAssetCatalogGateway
{
    private readonly List<Asset> _assets =
    [
        new("PUMP-102", "Primary Coolant Centrifugal Pump", "Grundfos", "CR-45-3", "SN-88412", "Pump"),
        new("COMP-204", "Rotary Screw Air Compressor", "Atlas Copco", "GA-37-VSD", "SN-55219", "Compressor"),
        new("HVAC-09", "Roof Chiller Unit B", "Carrier", "30XA-120", "SN-99014", "HVAC")
    ];

    private readonly ConcurrentDictionary<string, List<(string ManualId, double Confidence, MappingStatus Status)>> _links = new();

    public Task<IReadOnlyList<Asset>> SearchAssetsAsync(
        string? manufacturer,
        string? modelKeyword,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<Asset> query = _assets;

        if (!string.IsNullOrWhiteSpace(manufacturer))
        {
            query = query.Where(a => a.Manufacturer.Contains(manufacturer, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(modelKeyword))
        {
            query = query.Where(a =>
                a.ModelNumber.Contains(modelKeyword, StringComparison.OrdinalIgnoreCase) ||
                a.Name.Contains(modelKeyword, StringComparison.OrdinalIgnoreCase));
        }

        return Task.FromResult<IReadOnlyList<Asset>>(query.ToList());
    }

    public Task<Asset?> GetAssetByIdAsync(string assetId, CancellationToken cancellationToken = default)
    {
        var asset = _assets.FirstOrDefault(a => a.Id.Equals(assetId, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(asset);
    }

    public Task LinkManualToAssetAsync(
        string assetId,
        string manualId,
        double confidenceScore,
        MappingStatus status,
        CancellationToken cancellationToken = default)
    {
        var list = _links.GetOrAdd(assetId, _ => []);
        list.Add((manualId, confidenceScore, status));
        return Task.CompletedTask;
    }
}