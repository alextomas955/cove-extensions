using WhisparrSync.Contracts;
using WhisparrSync.Missing;

namespace WhisparrSync.Tests.TestSupport;

// The name is what a provider's exact-name lookup is offered, so a case states the name it offers
// rather than seeding a row to be read back. Reads records a case that reached this without
// expecting to.
internal sealed class StubEntityNames(EntityName? named = null) : IEntityNamePort
{
    public List<(WhisparrEntityKind Kind, int CoveId)> Reads { get; } = [];

    public Task<EntityName?> ReadNameAsync(
        WhisparrEntityKind kind, int coveId, CancellationToken ct)
    {
        Reads.Add((kind, coveId));
        return Task.FromResult(named);
    }
}
