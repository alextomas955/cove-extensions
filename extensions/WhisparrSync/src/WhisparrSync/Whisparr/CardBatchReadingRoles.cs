using WhisparrSync.Contracts;

namespace WhisparrSync.Whisparr;

/// <summary>What an instance holds for one card.</summary>
/// <remarks>A null member is one the answering row carried no value for.</remarks>
public sealed record WhisparrHeldCard(bool? Monitored, bool? HasFile);

/// <summary>What one batch read established about the identifiers it was asked about.</summary>
/// <remarks>
/// <paramref name="Held"/> names only the identifiers the instance answered a row for, spelled as
/// the caller asked them. An identifier in neither member is the instance stating it holds none.
/// <para>
/// <paramref name="NotAnswered"/> names the identifiers this read could not speak for at all, which
/// is not the same as an absence: one generation addresses an entity by a number it issues itself,
/// so an identifier that is not that number is outside what its list can answer. A caller asks about
/// those one at a time instead.
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
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        WhisparrEntityKind kind,
        IReadOnlyList<string> foreignIds,
        CancellationToken ct);
}

/// <summary>Reads what the instance holds for many scenes in one request.</summary>
/// <inheritdoc cref="IWhisparrEntityBatchReading" path="/remarks"/>
public interface IWhisparrSceneBatchReading
{
    /// <summary>Reads which of <paramref name="foreignIds"/> the instance holds, and how.</summary>
    /// <inheritdoc cref="IWhisparrEntityBatchReading.ReadHeldEntitiesAsync" path="/exception"/>
    Task<WhisparrHeldCards> ReadHeldSceneCardsAsync(
        Uri baseAddress, string apiKey, IReadOnlyList<string> foreignIds, CancellationToken ct);
}
