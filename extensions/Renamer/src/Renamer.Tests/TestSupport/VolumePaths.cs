using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Tests.TestSupport;

// Path literals on named volumes, for the suites whose subject is the same-volume/cross-volume split.
//
// Volume identity is per-platform: the path root on Windows, the enclosing mount point on Unix. A
// POSIX-only literal therefore has the single "/" root on Windows and every item folds as
// same-volume, so distinct volumes come from drive letters there and from a fixed synthetic mount
// table here. The real mount table would make a cross-volume assertion machine-dependent.
internal static class VolumePaths
{
    /// <summary>A path named for the volume it sits on: <c>C:\dir\name</c> or <c>/c/dir/name</c>.</summary>
    public static string OnVol(string vol, string name) =>
        OperatingSystem.IsWindows() ? $@"{vol}:\dir\{name}" : $"/{vol.ToLowerInvariant()}/dir/{name}";

    // Every Unix path shares the "/" root, so these mounts carry the volume identity a cross-volume
    // case needs, standing in for the drive letters the Windows literals use. On Windows the real
    // table is never consulted for a drive-rooted path, so the argument is null there.
    public static readonly IReadOnlyCollection<string>? Mounts =
        OperatingSystem.IsWindows() ? null : ["/", "/c", "/d", "/e"];

    /// <summary>The volume key <see cref="OnVol"/> resolves to for <paramref name="vol"/>.</summary>
    public static string RootOf(string vol) => VolumeClassifier.VolumeKey(OnVol(vol, "x"), Mounts);

    // The shipped path budget, read from the options rather than transcribed, so a change to it moves
    // the overflow cases rather than leaving them beside a boundary they no longer sit on.
    public static readonly int Budget = new RenamerOptions().FullPathMax;

    // OnVol yields "C:\dir\{name}" on Windows and "/c/dir/{name}" on Unix - seven characters either
    // way, which is what lets one arithmetic land on the same absolute length on both platforms.
    private const int OnVolPrefixLength = 7;

    /// <summary>A basename that makes <see cref="OnVol"/> exactly <paramref name="total"/> long.</summary>
    public static string NameForPathLength(int total) =>
        new string('n', total - OnVolPrefixLength - ".mkv".Length) + ".mkv";
}
