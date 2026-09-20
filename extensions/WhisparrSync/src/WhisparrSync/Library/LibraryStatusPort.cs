using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Library;

// The reads run one after another. Issuing them together would put a page of parallel requests
// against a third party on every press of one control.
// Nothing is enumerated: each read names one entity the caller asked about, so a page of cards costs
// one read per card whatever the instance's catalogue holds.
// A contained failure writes one line naming its classification and the host, so a page of badges
// that drew nothing leaves a trace.
internal sealed class LibraryStatusPort(IEntityIdentityPort identities, ILogger log)
{
    // Costs at most one read per card, so it is bounded by the caller's own set and never by what
    // the instance holds. A card the library names no usable identifier for costs no read.
    // reading arrives per call, not per construction: which generation is connected is a stored
    // setting, so a role held from construction would predate the connection it describes.
    // A dropped read is reported apart from the readings, because a card the instance answered and
    // established nothing about looks exactly like a card whose read dropped.
    public async Task<(IReadOnlyList<LibraryStatusRow> Rows, bool AnyReadDropped)>
        ReadEntityCardsAsync(
            Func<string, CancellationToken, Task<WhisparrResponse>> reading,
            WhisparrEntityKind kind,
            WhisparrGeneration generation,
            Uri baseAddress,
            IReadOnlyList<int> coveIds,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(coveIds);

        var rows = new List<LibraryStatusRow>(coveIds.Count);
        var dropped = false;
        foreach (var coveId in coveIds)
        {
            var read = await ReadOneAsync(reading, kind, generation, baseAddress, coveId, ct)
                .ConfigureAwait(false);
            dropped |= read.Dropped;
            rows.Add(new LibraryStatusRow(coveId, read.Reading));
        }

        return (rows, dropped);
    }

    // Costs one exclusion read for the whole set plus one status read per identifier. The exclusion
    // read comes first, because a scene that is both excluded and unheld reads as excluded.
    // exclusions is a capability, not a role, so a generation registering none sends nothing and no
    // card is reported as excluded on a fact no instance answered.
    public async Task<(IReadOnlyDictionary<int, LibraryCardReading> Readings, bool AnyReadDropped)>
        ReadSceneCardsAsync(
            IWhisparrSceneStatusReading reading,
            Capability<IWhisparrSceneExclusionReading> exclusions,
            Uri baseAddress,
            string apiKey,
            WhisparrGeneration generation,
            IReadOnlyList<LibraryCardIdentity> identities,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(exclusions);
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(identities);

        var excluded = await ExcludedAmongAsync(exclusions, baseAddress, apiKey, identities, ct)
            .ConfigureAwait(false);

        var readings = new Dictionary<int, LibraryCardReading>(identities.Count);
        var dropped = false;
        foreach (var identity in identities)
        {
            var onList = excluded.Contains(identity.RemoteId);

            WhisparrResponse answered;
            try
            {
                answered = await reading
                    .ReadSceneByRemoteIdAsync(baseAddress, apiKey, identity.RemoteId, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception failure)
                when (failure is HttpRequestException or IOException or TaskCanceledException)
            {
                // Contained per card, as the entity path's is: a read that dropped part way through
                // one answer must not take the rest of the page's answers with it. An instance that
                // accepts the connection and then hangs outlives the client's own timeout, which is
                // told from a shutdown by the token and by nothing in the failure itself.
                WhisparrSyncLog.MonitoringRequestContained(
                    log, generation, WhisparrSyncLog.Classify(failure), baseAddress.Host);
                readings[identity.CoveId] = new LibraryCardReading(onList, null, null);
                dropped = true;
                continue;
            }

            readings[identity.CoveId] = SceneReading(answered, onList);
        }

        return (readings, dropped);
    }

    private static async Task<IReadOnlySet<string>> ExcludedAmongAsync(
        Capability<IWhisparrSceneExclusionReading> exclusions,
        Uri baseAddress,
        string apiKey,
        IReadOnlyList<LibraryCardIdentity> identities,
        CancellationToken ct)
        => await exclusions.Match(
            role => role.ReduceExclusionsAsync(
                baseAddress,
                apiKey,
                [.. identities.Select(identity => identity.RemoteId)],
                ct),
            _ => Task.FromResult<IReadOnlySet<string>>(NothingExcluded)).ConfigureAwait(false);

    // The per-scene route answers a held scene and an unheld one alike with a list, so a not-found
    // and an empty list are both the instance stating an absence rather than declining to answer.
    // Any other unsuccessful answer, and any body this cannot read, establishes no member.
    private static LibraryCardReading SceneReading(WhisparrResponse answered, bool excluded)
    {
        if (answered.StatusCode == 404)
        {
            return NotHeld(excluded);
        }

        if (answered.StatusCode is not (>= 200 and < 300))
        {
            return new LibraryCardReading(excluded, null, null);
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(answered.Body);
        }
        catch (JsonException)
        {
            return new LibraryCardReading(excluded, null, null);
        }

        if (parsed is not JsonArray held)
        {
            return new LibraryCardReading(excluded, null, null);
        }

        return held.Count == 0
            ? NotHeld(excluded)
            : new LibraryCardReading(
                excluded, true, FlagOn(held[0], "monitored"), FlagOn(held[0], "hasFile"));
    }

    // The instance answered that it holds no entry, and by the same answer no file either.
    private static LibraryCardReading NotHeld(bool excluded)
        => new(excluded, false, null, false);

    private static bool? FlagOn(JsonNode? row, string field)
        => row is JsonObject fields
            && fields[field] is JsonValue flag
            && flag.TryGetValue<bool>(out var value)
                ? value
                : null;

    private static IReadOnlySet<string> NothingExcluded { get; }
        = new HashSet<string>(StringComparer.Ordinal);

    // A library naming no single identifier in the connected namespace answers null rather than a
    // state: no state in the vocabulary means "cannot be asked about".
    private async Task<(LibraryCardReading? Reading, bool Dropped)> ReadOneAsync(
        Func<string, CancellationToken, Task<WhisparrResponse>> reading,
        WhisparrEntityKind kind,
        WhisparrGeneration generation,
        Uri baseAddress,
        int coveId,
        CancellationToken ct)
    {
        var identity = await identities.ResolveAsync(kind, coveId, generation, ct)
            .ConfigureAwait(false);
        if (identity.ForeignId is not { } foreignId)
        {
            return (null, false);
        }

        WhisparrResponse answered;
        try
        {
            answered = await reading(foreignId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
            when (failure is HttpRequestException or IOException or TaskCanceledException)
        {
            // Contained per card. A read that dropped part way through one answer must not take the
            // rest of the page's answers with it. An instance that accepts the connection and then
            // hangs outlives the client's own timeout, which is told from a shutdown by the token and
            // by nothing in the failure itself.
            WhisparrSyncLog.MonitoringRequestContained(
                log, generation, WhisparrSyncLog.Classify(failure), baseAddress.Host);
            return (Unestablished, true);
        }

        var presence = MonitoringProjector.PresenceOf(
            MonitoringProjector.Classify(answered).Reading, answered.Body);

        // Never excluded on this path: no entity exclusion reading role exists, so a true here would
        // be a fact no instance answered.
        return (new LibraryCardReading(false, presence.Present, presence.Monitored), false);
    }

    // The instance was asked and nothing about the entity was established.
    private static LibraryCardReading Unestablished { get; } = new(false, null, null);
}
