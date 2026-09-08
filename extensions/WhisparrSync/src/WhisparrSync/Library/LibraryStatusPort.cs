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
        IReadOnlyList<int> coveIds,
        CancellationToken ct);
}

/// <inheritdoc cref="ILibraryStatusPort"/>
/// <remarks>
/// The reads run one after another, matching the per-scene path this product already ships. Issuing
/// them together would put a page of parallel requests against a third party on every press of one
/// control.
/// <para>
/// Nothing is enumerated. Each read names one entity the caller asked about, so a page of forty
/// cards costs forty reads whatever the instance's own catalogue holds.
/// </para>
/// </remarks>
internal sealed class LibraryStatusPort(IEntityIdentityPort identities) : ILibraryStatusPort
{
    public async Task<IReadOnlyList<LibraryStatusRow>> ReadEntityCardsAsync(
        Func<string, CancellationToken, Task<WhisparrResponse>> reading,
        WhisparrEntityKind kind,
        WhisparrGeneration generation,
        IReadOnlyList<int> coveIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(coveIds);

        var rows = new List<LibraryStatusRow>(coveIds.Count);
        foreach (var coveId in coveIds)
        {
            rows.Add(new LibraryStatusRow(
                coveId,
                await ReadOneAsync(reading, kind, generation, coveId, ct).ConfigureAwait(false)));
        }

        return rows;
    }

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
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            // Contained per card. A read that dropped part way through one answer must not take the
            // rest of the page's answers with it.
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
