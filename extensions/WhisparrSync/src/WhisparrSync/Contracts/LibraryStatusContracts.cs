using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace WhisparrSync.Contracts;

/// <summary>Which kind of library card a status read is about.</summary>
/// <remarks>
/// Held apart from <see cref="Monitoring.WhisparrEntityKind"/> and never folded into it. Every arm
/// switching on that kind throws for one it cannot express, and a video is not an entity this
/// product monitors, so a member added there would reach a throw rather than an answer.
/// <para>
/// The converter is on the TYPE: an options-level one outranks a type attribute, so a second
/// declaration could drift and win in silence.
/// </para>
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum LibraryCardKind
{
    /// <summary>A scene card in a videos view.</summary>
    Video,

    /// <summary>A studio card.</summary>
    Studio,

    /// <summary>A performer card.</summary>
    Performer,
}

/// <summary>Why a whole page of cards cannot be answered for, or that it can.</summary>
/// <remarks>
/// Stated ONCE for the page rather than once per card. Forty copies of one sentence about the
/// connection is forty places for a reader to read the same fact.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum LibraryStatusRefusalKind
{
    /// <summary>Nothing was refused.</summary>
    None,

    /// <summary>No instance is configured, so no status could be read.</summary>
    NoInstanceConnected,

    /// <summary>
    /// The connected generation registers no role that answers for this card kind, so nothing was
    /// sent.
    /// </summary>
    /// <remarks>
    /// An absence rather than a decision. The instance was never asked, so a value saying it
    /// declined would name the wrong party.
    /// </remarks>
    WhisparrCannotAnswerForThisKind,

    /// <summary>The instance was asked and no whole answer arrived.</summary>
    InstanceUnreachable,
}

/// <summary>Which cards one status read is about.</summary>
/// <remarks>
/// The identifiers are Cove's own. Which identifier the connected instance is given is re-resolved
/// on the server from the library's own identity row under a host rule the browser does not hold, so
/// a caller naming a third party's identifier is not expressible here.
/// </remarks>
/// <param name="CoveIds">The cards on one rendered page, bounded by what one page carries.</param>
public sealed record LibraryStatusRequest(IReadOnlyList<int> CoveIds);

/// <summary>What the connected instance holds for one card.</summary>
/// <remarks>
/// The three members are exactly what the browser's state derivation takes, so nothing is derived
/// twice and the server never names the state a card draws.
/// </remarks>
/// <param name="Excluded">
/// Whether the instance's user has excluded it. Always false on the studio and performer path: this
/// extension holds no entity exclusion reading role, and inventing one would report a fact no
/// instance answered.
/// </param>
/// <param name="Present">
/// Whether the instance holds an entry for the entity, or null where nothing was established.
/// </param>
/// <param name="Monitored">The instance's own flag, or null where nothing was established.</param>
public sealed record LibraryCardReading(bool Excluded, bool? Present, bool? Monitored);

/// <summary>One requested card's answer.</summary>
/// <param name="CoveId">The card asked about.</param>
/// <param name="Reading">
/// What the instance holds, or null where this extension cannot speak for the card at all. A null
/// reading draws no badge; it is not a state and never reads as one.
/// </param>
public sealed record LibraryStatusRow(int CoveId, LibraryCardReading? Reading);

/// <summary>What one page of cards holds, as the badges read it.</summary>
/// <remarks>
/// One row per requested identifier, in the order requested, so the row count is the caller's own
/// and never grows with the library.
/// </remarks>
/// <param name="Rows">One row per requested card.</param>
/// <param name="Refusal">Why the whole page cannot be answered for, or that it can.</param>
public sealed record LibraryStatusView(
    IReadOnlyList<LibraryStatusRow> Rows, LibraryStatusRefusalKind Refusal);
