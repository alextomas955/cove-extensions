namespace Renamer.Tests.TestSupport;

// Observes the in-flight copy name CrossVolumeMover mints. The name carries random characters, so a
// test cannot know it any other way, and the mover's own post-copy seam is the only place it is
// visible.
internal static class InFlightCopies
{
    // A seam that records the minted path and leaves the copy exactly as production left it.
    public static Func<string, CancellationToken, Task> Recorder(List<string> minted) =>
        (inFlight, _) =>
        {
            minted.Add(inFlight);
            return Task.CompletedTask;
        };

    // Every path the mover minted is gone. The non-empty check comes first: the seam firing is what
    // makes the loop assert anything at all.
    public static void AssertAllRemoved(List<string> minted, string because)
    {
        Assert.NotEmpty(minted);
        foreach (var path in minted)
        {
            Assert.False(File.Exists(path), $"{because}: {path}");
        }
    }
}
