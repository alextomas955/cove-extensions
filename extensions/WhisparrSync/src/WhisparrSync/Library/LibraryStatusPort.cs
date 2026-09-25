using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Library;

// The reads run one after another: issuing them together would put a page of parallel requests
// against a third party on every press of one control. Nothing is enumerated, each read naming one
// entity the caller asked about, so a page of cards costs one read per card whatever the catalogue
// holds. A contained failure writes one line naming its classification and the host, so a page of
// badges that drew nothing leaves a trace.
internal sealed class LibraryStatusPort(IEntityIdentityPort identities, ILogger log)
{
    // Costs at most one read per card, so it is bounded by the caller's set and never by what the
    // instance holds; a card the library names no usable identifier for costs none. The reading
    // arrives per call, not per construction: which generation is connected is a stored setting, so
    // a role held from construction would predate the connection. A dropped read is reported apart
    // from the readings, a card the instance answered and established nothing about looking exactly
    // like one whose read dropped.
    public async Task<(IReadOnlyList<LibraryStatusRow> Rows, bool AnyReadDropped)>
        ReadEntityCardsAsync(
            Func<string, CancellationToken, Task<WhisparrResponse>> reading,
            IWhisparrEntityBatchReading? batch,
            WhisparrEntityKind kind,
            WhisparrBinding binding,
            IReadOnlyList<int> coveIds,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(coveIds);

        var named = new List<(int CoveId, string? ForeignId)>(coveIds.Count);
        foreach (var coveId in coveIds)
        {
            var identity = await identities.ResolveAsync(kind, coveId, binding.Generation, ct)
                .ConfigureAwait(false);
            named.Add((coveId, identity.ForeignId));
        }

        var batched = await BatchedAsync(batch, kind, binding, named, ct).ConfigureAwait(false);

        var rows = new List<LibraryStatusRow>(coveIds.Count);
        var dropped = batched.Dropped;
        foreach (var (coveId, foreignId) in named)
        {
            // A library naming no single identifier in the connected namespace answers null rather
            // than a state: no state in the vocabulary means "cannot be asked about".
            if (foreignId is null)
            {
                rows.Add(new LibraryStatusRow(coveId, null));
                continue;
            }

            if (batched.Cards is { } cards && !cards.NotAnswered.Contains(foreignId))
            {
                rows.Add(new LibraryStatusRow(coveId, EntityReading(cards, foreignId)));
                continue;
            }

            // A batch that dropped speaks for no card, and sending a read per card behind it would
            // cost the page exactly what the batch was there to avoid.
            if (batched.Dropped)
            {
                rows.Add(new LibraryStatusRow(coveId, Unestablished));
                continue;
            }

            // Either the generation registers no batch role, or the batch could not speak for this
            // identifier. Both are asked about one at a time.
            var read = await ReadOneAsync(reading, binding, foreignId, ct).ConfigureAwait(false);
            dropped |= read.Dropped;
            rows.Add(new LibraryStatusRow(coveId, read.Reading));
        }

        return (rows, dropped);
    }

    // One read for the whole page where the generation registers the role. A contained failure
    // reports no card rather than sending a read per card behind it: the page would then cost what
    // the batch was there to avoid, against an instance that just failed.
    private async Task<(WhisparrHeldCards? Cards, bool Dropped)> BatchedAsync(
        IWhisparrEntityBatchReading? batch,
        WhisparrEntityKind kind,
        WhisparrBinding binding,
        IReadOnlyList<(int CoveId, string? ForeignId)> named,
        CancellationToken ct)
    {
        if (batch is not { } reading)
        {
            return (null, false);
        }

        List<string> asked = [.. named.Select(row => row.ForeignId).OfType<string>().Distinct()];
        if (asked.Count == 0)
        {
            return (WhisparrHeldCards.Empty, false);
        }

        try
        {
            return (
                await reading.ReadHeldEntitiesAsync(kind, asked, ct).ConfigureAwait(false),
                false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
            when (failure is HttpRequestException or IOException or TaskCanceledException)
        {
            Contained(binding, failure);
            return (null, true);
        }
    }

    // An identifier the answer carries no row for is the instance stating it holds none. Never
    // excluded on this path: no entity exclusion reading role exists, so a true here would be a
    // fact no instance answered.
    private static LibraryCardReading EntityReading(WhisparrHeldCards cards, string foreignId)
        => cards.Held.TryGetValue(foreignId, out var held)
            ? new LibraryCardReading(false, true, held.Monitored)
            : new LibraryCardReading(false, false, null);

    // Costs one exclusion read for the whole set, plus either one batch read for the page or one
    // status read per identifier where the instance declares no batch role. The exclusion read
    // comes first, a scene that is both excluded and unheld reading as excluded. exclusions is null
    // where the instance declares no exclusion read, so that generation sends nothing and no card
    // is reported excluded on a fact no instance answered.
    public async Task<(IReadOnlyDictionary<int, LibraryCardReading> Readings, bool AnyReadDropped)>
        ReadSceneCardsAsync(
            IWhisparrSceneStatusReading reading,
            IWhisparrSceneExclusionReading? exclusions,
            IWhisparrSceneBatchReading? batch,
            WhisparrBinding binding,
            IReadOnlyList<LibraryCardIdentity> identities,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(identities);

        var excluded = await ExcludedAmongAsync(exclusions, identities, ct).ConfigureAwait(false);

        var batched = await BatchedScenesAsync(batch, binding, identities, ct)
            .ConfigureAwait(false);

        var readings = new Dictionary<int, LibraryCardReading>(identities.Count);
        var dropped = batched.Dropped;
        foreach (var identity in identities)
        {
            var onList = excluded.Contains(identity.RemoteId);

            if (batched.Dropped)
            {
                readings[identity.CoveId] = new LibraryCardReading(onList, null, null);
                continue;
            }

            if (batched.Cards is { } cards && !cards.NotAnswered.Contains(identity.RemoteId))
            {
                readings[identity.CoveId] = cards.Held.TryGetValue(identity.RemoteId, out var held)
                    ? new LibraryCardReading(onList, true, held.Monitored, held.HasFile)
                    : NotHeld(onList);
                continue;
            }

            WhisparrResponse answered;
            try
            {
                answered = await reading.ReadSceneByRemoteIdAsync(identity.RemoteId, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception failure)
                when (failure is HttpRequestException or IOException or TaskCanceledException)
            {
                // Contained per card, as the entity path above records: a read that dropped part
                // way through one answer must not take the rest of the page with it.
                Contained(binding, failure);
                readings[identity.CoveId] = new LibraryCardReading(onList, null, null);
                dropped = true;
                continue;
            }

            readings[identity.CoveId] = SceneReading(answered, onList);
        }

        return (readings, dropped);
    }

    // One read for the whole page where the instance declares the role, contained for the reason
    // the entity path's batch gives.
    private async Task<(WhisparrHeldCards? Cards, bool Dropped)> BatchedScenesAsync(
        IWhisparrSceneBatchReading? batch,
        WhisparrBinding binding,
        IReadOnlyList<LibraryCardIdentity> identities,
        CancellationToken ct)
    {
        if (batch is not { } reading)
        {
            return (null, false);
        }

        List<string> asked = [.. identities.Select(identity => identity.RemoteId).Distinct()];
        if (asked.Count == 0)
        {
            return (WhisparrHeldCards.Empty, false);
        }

        try
        {
            return (
                await reading.ReadHeldSceneCardsAsync(asked, ct).ConfigureAwait(false),
                false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
            when (failure is HttpRequestException or IOException or TaskCanceledException)
        {
            Contained(binding, failure);
            return (null, true);
        }
    }

    // One line per contained failure, naming its classification and the host, so a page of badges
    // that drew nothing leaves a trace.
    private void Contained(WhisparrBinding binding, Exception failure)
        => WhisparrSyncLog.MonitoringRequestContained(
            log,
            binding.Generation,
            WhisparrSyncLog.Classify(failure),
            binding.BaseAddress.Host);

    private static async Task<IReadOnlySet<string>> ExcludedAmongAsync(
        IWhisparrSceneExclusionReading? exclusions,
        IReadOnlyList<LibraryCardIdentity> identities,
        CancellationToken ct)
        => exclusions is { } role
            ? await role.ReduceExclusionsAsync(
                [.. identities.Select(identity => identity.RemoteId)],
                ct).ConfigureAwait(false)
            : NothingExcluded;

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

    private async Task<(LibraryCardReading? Reading, bool Dropped)> ReadOneAsync(
        Func<string, CancellationToken, Task<WhisparrResponse>> reading,
        WhisparrBinding binding,
        string foreignId,
        CancellationToken ct)
    {
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
            // Contained per card, as the entity path above records. An instance that accepts the
            // connection and then hangs outlives the client's own timeout, which is told from a
            // shutdown by the token and by nothing in the failure itself.
            Contained(binding, failure);
            return (Unestablished, true);
        }

        var presence = MonitoringProjector.PresenceOf(
            MonitoringProjector.Classify(answered).Reading, answered.Body);

        // Never excluded on this path, for the reason EntityReading gives.
        return (new LibraryCardReading(false, presence.Present, presence.Monitored), false);
    }

    // The instance was asked and nothing about the entity was established.
    private static LibraryCardReading Unestablished { get; } = new(false, null, null);
}
