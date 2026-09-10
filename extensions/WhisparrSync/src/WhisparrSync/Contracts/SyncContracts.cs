using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace WhisparrSync.Contracts;

/// <summary>What a sync run registers in the connected instance.</summary>
/// <remarks>
/// Declared here rather than derived from the generation on the page, because this is a wire type
/// and its spelling is part of this extension's contract. The converter is on the TYPE: an
/// options-level one outranks a type attribute, so a second declaration could drift and win in
/// silence.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum SyncRegisters
{
    /// <summary>One entry per scene the library holds.</summary>
    Scenes,

    /// <summary>One entry per site the library's scenes came from.</summary>
    Sites,
}

/// <summary>Why the sync surface cannot answer or cannot act, or that it can.</summary>
/// <remarks>
/// The backend answers with a kind. The sentence a user reads is a frontend constant, so nothing an
/// instance said can reach the copy.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum SyncRefusalKind
{
    /// <summary>Nothing was refused.</summary>
    None,

    /// <summary>No instance is configured, so nothing could be counted or registered.</summary>
    NoInstanceConnected,

    /// <summary>
    /// The connected generation keeps no per-scene records, so a per-scene comparison has nothing
    /// to ask about.
    /// </summary>
    /// <remarks>
    /// Its own kind because nothing clears it: a retry would read the same absence. A reader offered
    /// a retry here would be offered a gesture that cannot change the answer.
    /// </remarks>
    WhisparrKeepsNoSceneRecords,

    /// <summary>
    /// Nothing has been counted, so a run cannot say how many scenes it would offer.
    /// </summary>
    CountFirst,

    /// <summary>A run is already in flight, and a second one would offer the same scenes again.</summary>
    AlreadyRunning,
}

/// <summary>What one count found, and when it found it.</summary>
/// <param name="NotYetThere">How many identified scenes the instance does not hold.</param>
/// <param name="AlreadyThere">How many identified scenes it already holds.</param>
/// <param name="Skipped">How many scenes carry no identifier the instance names entries by.</param>
/// <param name="Registers">What a run would register in the instance.</param>
/// <param name="CountedAt">When the count was taken, so the page can state its age.</param>
public sealed record SyncPreviewView(
    int NotYetThere,
    int AlreadyThere,
    int Skipped,
    SyncRegisters Registers,
    DateTimeOffset CountedAt);

/// <summary>One read of the count slot, and whether a run is in flight behind it.</summary>
/// <remarks>
/// The three counts ride one member, so a read cannot answer two of them. A view missing one number
/// would render as a zero, which is a confident report this product cannot support.
/// </remarks>
/// <param name="View">The counts held, or null where none are in date.</param>
/// <param name="Refusal">Why nothing can be counted, or that something can.</param>
/// <param name="SyncRunning">Whether one of this extension's own sync runs is pending or running.</param>
public sealed record SyncPreviewRead(
    SyncPreviewView? View, SyncRefusalKind Refusal, bool SyncRunning);

/// <summary>What one library run was asked to do.</summary>
/// <remarks>
/// The monitor choice travels with the press rather than being stored: it is the reader's decision
/// about this run, and a stored one would apply to a run started from somewhere else.
/// </remarks>
/// <param name="AlsoMonitor">
/// Whether the scenes the run offers should also be marked wanted. Absent reads as false, so a
/// caller that names nothing monitors nothing.
/// </param>
public sealed record SyncRunRequest(bool AlsoMonitor);

/// <summary>The job id a sync enqueue answered with, or why it answered none.</summary>
/// <param name="JobId">What this extension's own status route answers about, or null on a refusal.</param>
/// <param name="Refusal">Why nothing was started, or that something was.</param>
public sealed record SyncEnqueued(string? JobId, SyncRefusalKind Refusal);
