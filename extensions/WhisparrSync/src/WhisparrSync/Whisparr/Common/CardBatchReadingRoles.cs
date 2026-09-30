using WhisparrSync.Contracts;

namespace WhisparrSync.Whisparr;

/// <summary>What an instance holds for one card.</summary>
/// <remarks>A null member is one the answering row carried no value for.</remarks>
public sealed record WhisparrHeldCard(bool? Monitored, bool? HasFile);

/// <summary>What one site lookup established about the site it named.</summary>
/// <remarks>
/// An unreadable answer is apart from an absence on purpose: a body that is not a list of sites is
/// the instance saying nothing, and reporting it as an absence states a fact no instance answered.
/// </remarks>
public sealed record SiteLookupReading(bool Readable, WhisparrHeldCard? Held)
{
    /// <summary>The answer could not be read, so nothing was established.</summary>
    public static SiteLookupReading Unreadable { get; } = new(false, null);

    /// <summary>The instance answered, and holds no site under that identifier.</summary>
    public static SiteLookupReading HoldsNone { get; } = new(true, null);

    /// <summary>The instance holds the site, as <paramref name="card"/> describes it.</summary>
    public static SiteLookupReading Holding(WhisparrHeldCard card) => new(true, card);
}

/// <summary>What one batch read established about the identifiers it was asked about.</summary>
/// <remarks>
/// <paramref name="Held"/> names only the identifiers the instance answered a row for, spelled as
/// the caller asked them. An identifier in neither member is the instance stating it holds none.
/// <para>
/// <paramref name="NotAnswered"/> names the identifiers this read could not speak for at all, which
/// is not the same as an absence: an absence is the instance stating it holds none, and an answer
/// that could not be read states nothing. A caller asks about those one at a time instead.
/// </para>
/// </remarks>
public sealed record WhisparrHeldCards(
    IReadOnlyDictionary<string, WhisparrHeldCard> Held,
    IReadOnlySet<string> NotAnswered)
{
    /// <summary>Nothing was asked, so nothing is held and nothing is left unanswered.</summary>
    public static WhisparrHeldCards Empty { get; } = new(
        new Dictionary<string, WhisparrHeldCard>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>Reads what the instance holds for many entities in one request.</summary>
/// <remarks>
/// The answer is bounded by the identifiers asked about and never by the instance's holdings, so a
/// page of cards costs one request whatever the catalogue holds.
/// </remarks>
public interface IWhisparrEntityBatchReading
{
    /// <summary>Reads which of <paramref name="foreignIds"/> the instance holds, and how.</summary>
    /// <exception cref="HttpRequestException">
    /// Nothing whole arrived, or the answer could not be read. Raised rather than answered as an
    /// empty set: an empty set reports every identifier asked about as one the instance holds none
    /// of, which is the opposite of the truth.
    /// </exception>
    Task<WhisparrHeldCards> ReadHeldEntitiesAsync(
        WhisparrEntityKind kind, IReadOnlyList<string> foreignIds, CancellationToken ct);
}

/// <summary>Reads what the instance holds for many scenes in one request.</summary>
/// <inheritdoc cref="IWhisparrEntityBatchReading" path="/remarks"/>
public interface IWhisparrSceneBatchReading
{
    /// <summary>Reads which of <paramref name="foreignIds"/> the instance holds, and how.</summary>
    /// <inheritdoc cref="IWhisparrEntityBatchReading.ReadHeldEntitiesAsync" path="/exception"/>
    Task<WhisparrHeldCards> ReadHeldSceneCardsAsync(
        IReadOnlyList<string> foreignIds, CancellationToken ct);
}
