using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Library;

/// <summary>What the connected instance holds for each card on one page.</summary>
public interface ILibraryStatusPort
{
    /// <summary>
    /// What the instance holds for each of <paramref name="coveIds"/>, one row per identifier in the
    /// order they were given.
    /// </summary>
    /// <remarks>
    /// Costs at most one read per card, so it is bounded by the caller's own set and never by what
    /// the instance holds. A card the library names no usable identifier for costs no read at all.
    /// <para>
    /// <c>reading</c> arrives per call rather than per construction, already aimed at the connected
    /// instance. Which generation is connected is a stored setting, so a role held from construction
    /// would be one obtained before the connection it describes was known. It takes no route, no
    /// verb and no query key, so nothing here can replace the request that is issued.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<LibraryStatusRow>> ReadEntityCardsAsync(
        Func<string, CancellationToken, Task<WhisparrResponse>> reading,
        WhisparrEntityKind kind,
        WhisparrGeneration generation,
        Uri baseAddress,
        IReadOnlyList<int> coveIds,
        CancellationToken ct);

    /// <summary>
    /// What the instance holds for each of <paramref name="identities"/>, keyed by the Cove id each
    /// was resolved from.
    /// </summary>
    /// <remarks>
    /// Costs one exclusion read for the whole set plus one status read per identifier. The exclusion
    /// read comes first, because exclusion is tested before a state is derived and a scene that is
    /// both excluded and unheld reads as excluded.
    /// <para>
    /// <paramref name="exclusions"/> is a capability rather than a role, so a generation registering
    /// none is expressible here: nothing is obtained, nothing is sent, and no card is reported as
    /// excluded on a fact no instance answered.
    /// </para>
    /// </remarks>
    Task<IReadOnlyDictionary<int, LibraryCardReading>> ReadSceneCardsAsync(
        IWhisparrSceneStatusReading reading,
        Capability<IWhisparrSceneExclusionReading> exclusions,
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        IReadOnlyList<LibraryCardIdentity> identities,
        CancellationToken ct);
}

/// <inheritdoc cref="ILibraryStatusPort"/>
/// <remarks>
/// The reads run one after another, matching the per-scene path this product already ships. Issuing
/// them together would put a page of parallel requests against a third party on every press of one
/// control.
/// <para>
/// Nothing is enumerated. Each read names one entity the caller asked about, so a page of cards
/// costs one read per card whatever the instance's own catalogue holds.
/// </para>
/// <para>
/// A contained failure writes one line naming its classification and the host. Without it a page of
/// badges that quietly drew nothing leaves no trace at all, on the one surface where a press costs a
/// read per card.
/// </para>
/// </remarks>
internal sealed class LibraryStatusPort(IEntityIdentityPort identities, ILogger log)
    : ILibraryStatusPort
{
    public async Task<IReadOnlyList<LibraryStatusRow>> ReadEntityCardsAsync(
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
        foreach (var coveId in coveIds)
        {
            rows.Add(new LibraryStatusRow(
                coveId,
                await ReadOneAsync(reading, kind, generation, baseAddress, coveId, ct)
                    .ConfigureAwait(false)));
        }

        return rows;
    }

    public async Task<IReadOnlyDictionary<int, LibraryCardReading>> ReadSceneCardsAsync(
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
                continue;
            }

            readings[identity.CoveId] = SceneReading(answered, onList);
        }

        return readings;
    }

    /// <summary>
    /// Which of <paramref name="identities"/> the instance's user has excluded, or none where the
    /// generation registers no role to ask.
    /// </summary>
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

    /// <summary>What one scene answer establishes about the instance.</summary>
    /// <remarks>
    /// The per-scene route answers a held scene and an unheld one alike with a list, so a not-found
    /// and an empty list are both the instance stating an absence rather than declining to answer.
    /// Any other unsuccessful answer, and any body this cannot read, establishes neither member.
    /// </remarks>
    private static LibraryCardReading SceneReading(WhisparrResponse answered, bool excluded)
    {
        if (answered.StatusCode == 404)
        {
            return new LibraryCardReading(excluded, false, null);
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
            ? new LibraryCardReading(excluded, false, null)
            : new LibraryCardReading(excluded, true, MonitoredFlagOn(held[0]));
    }

    /// <summary>The row's own monitored flag, or null where it carries no usable one.</summary>
    private static bool? MonitoredFlagOn(JsonNode? row)
        => row is JsonObject fields
            && fields["monitored"] is JsonValue flag
            && flag.TryGetValue<bool>(out var monitored)
                ? monitored
                : null;

    /// <summary>Nothing was asked, so nothing is excluded.</summary>
    private static IReadOnlySet<string> NothingExcluded { get; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>One card's reading, or null where the extension cannot speak for it.</summary>
    /// <remarks>
    /// A library naming no single identifier in the connected generation's namespace answers null
    /// rather than a state. No state in the vocabulary means "cannot be asked about", and reusing one
    /// that means something else is the confident wrong claim this product refuses to make.
    /// </remarks>
    private async Task<LibraryCardReading?> ReadOneAsync(
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
            return null;
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
            return Unestablished;
        }

        var presence = MonitoringProjector.PresenceOf(
            MonitoringProjector.Classify(answered).Reading, answered.Body);

        // Never excluded on this path: no entity exclusion reading role exists, so a true here would
        // be a fact no instance answered.
        return new LibraryCardReading(false, presence.Present, presence.Monitored);
    }

    /// <summary>The instance was asked and nothing about the entity was established.</summary>
    private static LibraryCardReading Unestablished { get; } = new(false, null, null);
}
