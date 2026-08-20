namespace Renamer.Execution;

/// <summary>
/// The EF shapes behind <c>IRevertJournal</c>, mapped to the extension-owned tables in
/// <c>Renamer.Data.cs</c> and created by <see cref="RevertJournalSchema"/>'s migration.
///
/// These are storage types, not a domain model: nothing outside <see cref="CoveRevertJournal"/> and
/// the model configuration should name them, because the port's records are what the rest of the
/// extension speaks in.
/// </summary>
/// <remarks>
/// Mutable properties with EF-friendly defaults, not records: the change tracker sets them on
/// materialization and the counter updates below are in-place writes on a tracked entity.
/// </remarks>
public sealed class RevertBatchEntity
{
    public string RunId { get; set; } = "";

    /// <summary>Server UTC ticks at which the batch opened.</summary>
    /// <remarks>
    /// Ticks in an integer column rather than a timestamp type, because the same DDL has to run on
    /// both the provider production uses and the one the tests use.
    /// </remarks>
    public long OpenedAtUtcTicks { get; set; }

    /// <summary>The run's <c>RenamerFileKind</c> by name.</summary>
    public string Kind { get; set; } = "";

    public int OriginalCount { get; set; }

    public int RestoredCount { get; set; }

    public int UnrestorableCount { get; set; }
}

/// <summary>One pending restore. Its identity is (<see cref="RunId"/>, <see cref="Seq"/>).</summary>
public sealed class RevertRowEntity
{
    public string RunId { get; set; } = "";

    public long Seq { get; set; }

    public int EntityId { get; set; }

    public int FileId { get; set; }

    public string OldPath { get; set; } = "";

    public string SidecarsJson { get; set; } = "";
}
