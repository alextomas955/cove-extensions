using WhisparrSync.Contracts;
using WhisparrSync.Missing;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>What the library calls an entity, without a library.</summary>
/// <remarks>
/// The name is what a provider's exact-name lookup is offered, so a case about that lookup states
/// the name it offers rather than seeding a row to be read back. A case reaching this when it did
/// not expect to is what <see cref="Reads"/> records.
/// </remarks>
internal sealed class StubEntityNames(EntityName? named = null) : IEntityNamePort
{
    /// <summary>Every entity a name was asked for, in order.</summary>
    public List<(WhisparrEntityKind Kind, int CoveId)> Reads { get; } = [];

    public Task<EntityName?> ReadNameAsync(
        WhisparrEntityKind kind, int coveId, CancellationToken ct)
    {
        Reads.Add((kind, coveId));
        return Task.FromResult(named);
    }
}
