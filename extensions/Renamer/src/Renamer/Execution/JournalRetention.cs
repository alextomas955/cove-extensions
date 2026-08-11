namespace Renamer.Execution;

/// <summary>How long the undo journal keeps a batch before it expires.</summary>
/// <remarks>
/// A CONSTANT, and deliberately not a setting. The window is the whole of the retention model, so
/// exposing it would multiply the states a user — and anyone reading a support report — has to reason
/// about, with no recovery benefit: an undo is a correction made minutes or hours after a rename, not
/// weeks after, and a longer window recovers nothing a shorter one lost. One number that is always the
/// same is also one number a panel can state plainly.
/// <para>
/// It bounds the journal in TIME rather than in row count. A row cap would convert a hard failure into a
/// silently truncated answer — a rename partly journalled reads exactly like one fully journalled, and
/// the undo that follows is quietly partial. Time cannot do that: a batch is either wholly inside the
/// window or wholly gone.
/// </para>
/// </remarks>
public static class JournalRetention
{
    /// <summary>The age past which a batch, and every row it still holds, is dropped.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);
}
