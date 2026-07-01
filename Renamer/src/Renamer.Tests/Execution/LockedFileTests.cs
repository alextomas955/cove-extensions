using Renamer.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// A locked/in-use source file (held open with <see cref="FileShare.None"/>)
/// is caught and reported as a skip — the move does NOT happen, NO exception escapes, the source
/// stays at its old path, and the locking process is NEVER touched (the helper references no
/// <c>System.Diagnostics.Process</c> API — it never tries to force a lock open).
/// Exercised against the real filesystem via the <see cref="TempDir"/> fixture.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class LockedFileTests
{
    [Fact]
    public void LockedSource_FileShareNone_SkippedNotThrown_SourceIntact()
    {
        using var dir = new TempDir();
        var old = dir.Touch("clip.mkv", "data");
        var dest = Path.Combine(dir.Root, "Renamerd.mkv");
        var mover = new DiskMover();

        // Hold the SOURCE open exclusively so File.Move throws IOException (ERROR_SHARING_VIOLATION).
        using (new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = mover.Move(old, dest);

            Assert.False(result.Moved);
            Assert.Equal(DiskMover.MoveOutcome.LockedOrExists, result.Outcome);
            Assert.NotNull(result.Reason);
        }

        // The source survives at its old path; the destination was never created.
        Assert.True(File.Exists(old), "locked source must remain at its old path");
        Assert.False(File.Exists(dest), "no destination must be created when the source is locked");
        Assert.Equal("data", File.ReadAllText(old));
    }

    [Fact]
    public void HappyMove_RealTempDir_OldGoneNewHasContent()
    {
        using var dir = new TempDir();
        var old = dir.Touch("clip.mkv", "hello");
        var dest = Path.Combine(dir.Root, "sub", "Renamerd.mkv");
        var mover = new DiskMover();

        var result = mover.Move(old, dest);

        Assert.True(result.Moved);
        Assert.Equal(DiskMover.MoveOutcome.Moved, result.Outcome);
        Assert.False(File.Exists(old), "old path must no longer exist after a move");
        Assert.True(File.Exists(dest), "new path must exist after a move (parent dir auto-created)");
        Assert.Equal("hello", File.ReadAllText(dest));
    }

    [Fact]
    public void DestinationExists_TwoArgMove_NeverOverwrites_Skipped()
    {
        using var dir = new TempDir();
        var old = dir.Touch("clip.mkv", "new");
        var dest = dir.Touch("Taken.mkv", "original");
        var mover = new DiskMover();

        var result = mover.Move(old, dest);

        // The 2-arg File.Move throws when the destination exists; the helper surfaces a skip.
        Assert.False(result.Moved);
        Assert.Equal(DiskMover.MoveOutcome.LockedOrExists, result.Outcome);
        // The pre-existing destination is left untouched (never clobbered) and the source survives.
        Assert.Equal("original", File.ReadAllText(dest));
        Assert.True(File.Exists(old));
        Assert.Equal("new", File.ReadAllText(old));
    }
}
