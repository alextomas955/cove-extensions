using Renamer.Execution;

namespace Renamer.Tests.Contracts;

/// <summary>
/// Pins the path-join and duplicate-suffix helpers at the input shapes on which the three private
/// implementations they replaced disagreed with each other. No store, no database context, no
/// filesystem — pure string math, which is the whole reason this suite needs no setup, no doubles and
/// no running service.
/// </summary>
/// <remarks>
/// PLACEMENT IS LOAD-BEARING, and this file is deliberately NOT under <c>Execution/</c>. The cove-absent
/// continuous-integration leg removes cove-dependent sources from those folders FILE BY FILE, so whether
/// a pure suite placed beside the code it covers keeps running there depends on a <c>Compile Remove</c>
/// entry nobody adds deliberately for a test that needs none — and this leg is where the repository's
/// coverage is thinnest. <c>Contracts/</c> is covered by no such entry at all, which is the guarantee.
/// <para>
/// WHY THIS CLASS EXISTS. Until it was written the suite contained no test of any path-join helper at
/// all, so a merge of three differing implementations into one could have changed behavior and still
/// left every test green — a green suite was an instrument that agreed with itself whatever the merge
/// did. Each expectation below was recorded by INVOKING the three shipping bodies (executor, planner,
/// replayer) through reflection before the merge, not by reading them, so it states what the code did
/// rather than what it looked like it did.
/// </para>
/// <para>
/// WHERE THE THREE DISAGREED, and which behavior the one surviving helper keeps:
/// <list type="bullet">
/// <item>a folder part ending in a BACKSLASH — executor <c>media\videos/clip.mkv</c>, planner
/// <c>media\videos\/clip.mkv</c> (a doubled separator), replayer <c>media/videos/clip.mkv</c>. The
/// replayer's is kept: it is the only one whose output is a valid single-separator path.</item>
/// <item>a basename with a LEADING backslash — only the executor trimmed it. The executor's is
/// kept.</item>
/// <item>an UNNORMALIZED folder path — only the replayer normalized, and it did so inside its own
/// body, which this merge deletes. The replayer's is kept, moved into the shared helper.</item>
/// <item>an EMPTY part — the replayer guarded neither, so an empty folder produced the ABSOLUTE path
/// <c>/clip.mkv</c> and an empty basename a trailing separator. The executor's and planner's guard is
/// kept.</item>
/// </list>
/// The union of those four choices is the superset, and it is what the assertions below state.
/// </para>
/// </remarks>
[Trait("Tier", "L0")]
public sealed class PathOpsLogicTests
{
    [Fact]
    public void TheOrdinaryShapeEveryCallerSuppliesToday_IsWhatAllThreeAlreadyProduced()
    {
        // All three implementations agreed on these before the merge; they are the regression floor.
        Assert.Equal("media/videos/clip.mkv", PathOps.JoinPath("media/videos", "clip.mkv"));
        Assert.Equal("media/videos/clip.mkv", PathOps.JoinPath("media/videos/", "clip.mkv"));

        // A multi-segment forward-slash right-hand part: the planner joins a rendered folder path,
        // not only a basename, so the helper must not assume its second argument is one segment.
        Assert.Equal("media/videos/Studio/2019/clip.mkv", PathOps.JoinPath("media/videos", "Studio/2019/clip.mkv"));
    }

    [Fact]
    public void ABackslashAtEitherBoundary_IsTrimmedRatherThanDoubled()
    {
        // Pre-merge: executor "media\videos/clip.mkv", planner "media\videos\/clip.mkv" (its TrimEnd
        // named only '/'), replayer "media/videos/clip.mkv". The replayer's is the kept behavior.
        Assert.Equal("media/videos/clip.mkv", PathOps.JoinPath("media\\videos\\", "clip.mkv"));

        // Pre-merge: only the executor trimmed a leading separator off the right-hand part; the other
        // two emitted "media/videos/\clip.mkv" and "media/videos//clip.mkv". The executor's is kept.
        Assert.Equal("media/videos/clip.mkv", PathOps.JoinPath("media/videos", "\\clip.mkv"));
        Assert.Equal("media/videos/clip.mkv", PathOps.JoinPath("media/videos", "/clip.mkv"));
    }

    [Fact]
    public void AnUnnormalizedFolderPath_IsNormalizedInsideTheHelperNotAtACallSite()
    {
        // The undo replayer's own shape: a parent folder path read back from the host database, which
        // on Windows arrives backslash-separated. Pre-merge the replayer normalized it in its own
        // body; the executor and planner did not, and emitted the mixed "C:\media\videos/clip.mkv".
        // The merge deletes that body, so the surviving helper must carry the normalization — this
        // assertion is what proves it does rather than a comment claiming it.
        Assert.Equal("C:/media/videos/clip.mkv", PathOps.JoinPath("C:\\media\\videos", "clip.mkv"));
    }

    [Fact]
    public void AnEmptyPart_YieldsTheOtherWithNoStraySeparator()
    {
        // A file at the library root has an empty parent folder path. Pre-merge the replayer, which
        // guarded neither part, turned that into the ABSOLUTE path "/clip.mkv" — a different file.
        Assert.Equal("clip.mkv", PathOps.JoinPath("", "clip.mkv"));
        Assert.Equal("media/videos", PathOps.JoinPath("media/videos", ""));
        Assert.Equal("", PathOps.JoinPath("", ""));

        // The ONE output that differs from all three pre-merge bodies, asserted deliberately rather
        // than discovered later: they returned the right-hand part untouched when the left was empty,
        // so a backslash in it survived. Normalizing unconditionally is the stronger invariant — the
        // result is canonical for every input — and no caller can reach this shape, because every
        // right-hand argument is either a basename or a rendered forward-slash folder path.
        Assert.Equal("sub/clip.mkv", PathOps.JoinPath("", "sub\\clip.mkv"));
    }

    [Fact]
    public void TheSuffixCounter_GoesBeforeTheExtension()
    {
        // The executor's and planner's bodies produced identical output on every case here, so this
        // group pins a merge of equals rather than a choice between unequals.
        Assert.Equal("name (1).mkv", PathOps.ApplySuffix("name", ".mkv", " ({n})", 1));
        Assert.Equal("name (12)", PathOps.ApplySuffix("name", "", " ({n})", 12));

        // Every occurrence of the token is replaced, and a format naming no token still concatenates
        // — neither is guarded against, and a user can configure both.
        Assert.Equal("name-3-3.mkv", PathOps.ApplySuffix("name", ".mkv", "-{n}-{n}", 3));
        Assert.Equal("nameno-token.mkv", PathOps.ApplySuffix("name", ".mkv", "no-token", 5));
    }
}
