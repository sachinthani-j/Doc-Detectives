namespace DocDetectives.Domain.Entities;

public sealed record Asset(
    string Id,
    string Name,
    string Manufacturer,
    string ModelNumber,
    string SerialNumber,
    string Category);