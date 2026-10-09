using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace WhisparrSync.Contracts;

/// <summary>Which kind of library card a status read is about.</summary>
/// <remarks>
/// Held apart from <see cref="WhisparrEntityKind"/> and never folded into it. Every arm switching on
/// that kind throws for one it cannot express, and a video is not an entity this product monitors,
/// so a member added there would reach a throw rather than an answer.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum LibraryCardKind
{
    /// <summary>A scene card in a videos view.</summary>
    Video,

    Studio,

    Performer,
}

/// <summary>Why a whole page of cards cannot be answered for, or that it can.</summary>
/// <remarks>Stated once for the page rather than once per card.</remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum LibraryStatusRefusalKind
{
    /// <summary>Nothing was refused.</summary>
    None,

    /// <summary>No instance is configured, so no status could be read.</summary>
    NoInstanceConnected,

    /// <summary>
    /// The connected generation registers no role that answers for this card kind, so nothing was
    /// sent. An absence rather than a decision: the instance was never asked.
    /// </summary>
    WhisparrCannotAnswerForThisKind,

    /// <summary>The instance was asked and no whole answer arrived.</summary>
    InstanceUnreachable,
}

/// <summary>Which cards one status read is about.</summary>
/// <remarks>
/// The identifiers are Cove's own, bounded by what one rendered page carries. Which identifier the
/// connected instance is given is re-resolved on the server from the library's own identity row, so
/// a caller naming a third party's identifier is not expressible here.
/// </remarks>
public sealed record LibraryStatusRequest(IReadOnlyList<int> CoveIds);

/// <summary>What the connected instance holds for one card.</summary>
/// <param name="Excluded">
/// Whether the instance's user has excluded it. Always false on the studio and performer path: this
/// extension holds no entity exclusion reading role, and inventing one would report a fact no
/// instance answered.
/// </param>
/// <param name="Present">
/// Whether the instance holds an entry for the entity, or null where nothing was established.
/// </param>
/// <param name="Monitored">The instance's own flag, or null where nothing was established.</param>
/// <param name="InLibrary">
/// Whether the instance holds a file for it, or null where nothing was established. Always null on
/// the studio and performer path: a file is a fact about one scene, and no answer on that path
/// carries one.
/// </param>
public sealed record LibraryCardReading(
    bool Excluded, bool? Present, bool? Monitored, bool? InLibrary = null);

/// <summary>One requested card's answer.</summary>
/// <remarks>
/// A null <c>Reading</c> is a card this extension cannot speak for at all. It draws no badge; it is
/// not a state and never reads as one.
/// </remarks>
public sealed record LibraryStatusRow(int CoveId, LibraryCardReading? Reading);

/// <summary>What one page of cards holds, as the badges read it.</summary>
/// <remarks>
/// One row per identifier answered for, in the order requested, so the row count is bounded by the
/// route's own page and never grows with the library. <c>MoreNotAnswered</c> says the request named
/// more cards than one page answers for; the cards with no row here are the ones to ask about
/// again, so a caller needs no figure of its own to reach every card.
/// <para>
/// The kind is carried because the route names it in a path segment, where no generated type can
/// reach it. Answered here it reaches the wire document as an enum, so the browser imports the
/// members instead of transcribing them.
/// </para>
/// </remarks>
public sealed record LibraryStatusView(
    LibraryCardKind Kind,
    IReadOnlyList<LibraryStatusRow> Rows,
    LibraryStatusRefusalKind Refusal,
    bool MoreNotAnswered = false);
