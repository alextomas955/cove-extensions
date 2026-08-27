using Renamer.Execution;

namespace Renamer.Tests.Contracts;

/// <summary>
/// Pins the path-join and duplicate-suffix helpers at the boundary shapes their callers - the
/// planner, the executor and the undo replayer - reach them with. Pure string math: no store, no
/// database context, no filesystem.
/// </summary>
/// <remarks>
/// Deliberately NOT under <c>Execution/</c>. The cove-absent CI leg removes that folder wholesale, so
/// a pure suite placed beside the code it covers stops running on the leg where this repository's
/// coverage is thinnest. <c>Contracts/</c> is named by no removal entry.
/// </remarks>
public sealed class PathOpsLogicTests
{
    [Fact]
    public void TheOrdinaryShapeEveryCallerSuppliesToday_IsWhatAllThreeAlreadyProduced()
    {
        Assert.Equal("media/videos/clip.mkv", PathOps.JoinPath("media/videos", "clip.mkv"));
        Assert.Equal("media/videos/clip.mkv", PathOps.JoinPath("media/videos/", "clip.mkv"));

        // The planner joins a rendered folder path rather than a basename, so the right-hand part is
        // not necessarily one segment.
        Assert.Equal("media/videos/Studio/2019/clip.mkv", PathOps.JoinPath("media/videos", "Studio/2019/clip.mkv"));
    }

    [Fact]
    public void ABackslashAtEitherBoundary_IsTrimmedRatherThanDoubled()
    {
        Assert.Equal("media/videos/clip.mkv", PathOps.JoinPath(@"media\videos\", "clip.mkv"));
        Assert.Equal("media/videos/clip.mkv", PathOps.JoinPath("media/videos", @"\clip.mkv"));
        Assert.Equal("media/videos/clip.mkv", PathOps.JoinPath("media/videos", "/clip.mkv"));
    }

    [Fact]
    public void AnUnnormalizedFolderPath_IsNormalizedInsideTheHelperNotAtACallSite()
    {
        // The undo replayer's shape: a parent folder path read back from the host database, which on
        // Windows arrives backslash-separated.
        Assert.Equal("C:/media/videos/clip.mkv", PathOps.JoinPath(@"C:\media\videos", "clip.mkv"));
    }

    [Fact]
    public void AnEmptyPart_YieldsTheOtherWithNoStraySeparator()
    {
        // A file at the library root has an empty parent folder path. Emitting "/clip.mkv" for it would
        // name a different file.
        Assert.Equal("clip.mkv", PathOps.JoinPath("", "clip.mkv"));
        Assert.Equal("media/videos", PathOps.JoinPath("media/videos", ""));
        Assert.Equal("", PathOps.JoinPath("", ""));
        Assert.Equal("sub/clip.mkv", PathOps.JoinPath("", @"sub\clip.mkv"));
    }

    [Fact]
    public void TheSuffixCounter_GoesBeforeTheExtension()
    {
        Assert.Equal("name (1).mkv", PathOps.ApplySuffix("name", ".mkv", " ({n})", 1));
        Assert.Equal("name (12)", PathOps.ApplySuffix("name", "", " ({n})", 12));

        // Both shapes are configurable, and neither is guarded against: every occurrence of the token
        // is replaced, and a format naming no token still concatenates.
        Assert.Equal("name-3-3.mkv", PathOps.ApplySuffix("name", ".mkv", "-{n}-{n}", 3));
        Assert.Equal("nameno-token.mkv", PathOps.ApplySuffix("name", ".mkv", "no-token", 5));
    }
}
