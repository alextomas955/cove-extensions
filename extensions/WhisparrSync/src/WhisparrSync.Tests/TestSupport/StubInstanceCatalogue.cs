using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// Stands in for the instance's own scene list for one entity, which is what the missing surface is
/// composed from.
/// </summary>
internal sealed class StubInstanceCatalogue : IWhisparrEntityCatalogueReading
{
    private readonly WhisparrEntityCatalogue _answer;

    internal StubInstanceCatalogue(params string[] sceneIds)
        => _answer = WhisparrEntityCatalogue.Listing([.. sceneIds.Select(id => Scene(id))]);

    internal StubInstanceCatalogue(IReadOnlyList<WhisparrCatalogueScene> scenes)
        => _answer = WhisparrEntityCatalogue.Listing(scenes);

    internal StubInstanceCatalogue(WhisparrCatalogueRefusal refusal)
        => _answer = WhisparrEntityCatalogue.Refused(refusal);

    internal int Reads { get; private set; }

    internal static WhisparrCatalogueScene Scene(
        string id, bool monitored = false, string? date = null)
        => new(id, id, date, null, null, null, [], [], monitored, HasFile: false);

    public Task<WhisparrEntityCatalogue> ReadEntityCatalogueAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        WhisparrEntityKind kind,
        string foreignId,
        CancellationToken ct)
    {
        Reads++;
        return Task.FromResult(_answer);
    }
}
