using DocDetectives.Domain.Entities;

namespace DocDetectives.Domain.Abstractions;

/// <summary>The characteristic master table.</summary>
public interface ICharacteristicCatalog
{
    Task<IReadOnlyList<CharacteristicDefinition>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds new definitions or replaces existing ones with the same Id (e.g. to add an alias).</summary>
    Task SaveAsync(IReadOnlyList<CharacteristicDefinition> definitions, CancellationToken cancellationToken = default);
}
