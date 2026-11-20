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

    public Task<Asset?> FindAssetBySerialNumberAsync(string serialNumber, CancellationToken cancellationToken = default)
    {
        var asset = _assets.FirstOrDefault(a => a.SerialNumber.Equals(serialNumber, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(asset);
    }

    public Task<IReadOnlyList<Asset>> GetChildAssetsAsync(string parentAssetId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Asset> children = _assets
            .Where(a => string.Equals(a.ParentAssetId, parentAssetId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Task.FromResult(children);
    }

    public Task CreateAssetsAsync(IReadOnlyList<Asset> assets, string manualId, CancellationToken cancellationToken = default)
    {
        lock (_assets)
        {
            foreach (var asset in assets)
            {
                _assets.RemoveAll(a => a.Id.Equals(asset.Id, StringComparison.OrdinalIgnoreCase));
                _assets.Add(asset);
                _links.GetOrAdd(asset.Id, _ => []).Add((manualId, 1.0, MappingStatus.Created));
            }
        }

        return Task.CompletedTask;
    }

    public Task UpdateCharacteristicsAsync(
        string assetId,
        IReadOnlyList<AssetCharacteristic> characteristics,
        CancellationToken cancellationToken = default)
    {
        lock (_assets)
        {
            var index = _assets.FindIndex(a => a.Id.Equals(assetId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                _assets[index] = _assets[index] with { Characteristics = characteristics };
            }
        }

        return Task.CompletedTask;
    }

    public Task UpdateManufacturerDetailsAsync(
        string assetId,
        string? manufacturerName,
        int? manufactureYear,
        CancellationToken cancellationToken = default)
    {
        lock (_assets)
        {
            var index = _assets.FindIndex(a => a.Id.Equals(assetId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                _assets[index] = _assets[index] with
                {
                    ManufacturerName = manufacturerName ?? _assets[index].ManufacturerName,
                    ManufactureYear = manufactureYear ?? _assets[index].ManufactureYear
                };
            }
        }

        return Task.CompletedTask;
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