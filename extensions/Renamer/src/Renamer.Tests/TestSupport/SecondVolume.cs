using Renamer.Planner;

namespace Renamer.Tests.TestSupport;

// A directory on a different filesystem from the temp tree, so a move into it takes the
// cross-volume path. In precedence order: the COVE_TEST_SECOND_VOLUME directory (the only arm on
// macOS), a subst drive on Windows, a directory under the /dev/shm tmpfs on Linux. It is a second
// filesystem, not a second physical disk.
public sealed class SecondVolume : IDisposable
{
    private const string ShmRoot = "/dev/shm";

    // The environment variable naming a directory on a second filesystem.
    public const string OverrideVariable = "COVE_TEST_SECOND_VOLUME";

    // The root to place a cross-volume destination under.
    public string Root { get; }

    private readonly SubstDrive? _subst;
    private readonly string? _directory;

    public static bool IsAvailable =>
        OverridePath is string p && Directory.Exists(p)
        || OperatingSystem.IsWindows()
        || Directory.Exists(ShmRoot);

    public const string UnavailableReason =
        "needs a second filesystem: " + OverrideVariable +
        " naming a directory on one, a subst drive on Windows, or /dev/shm on Unix";

    private static string? OverridePath
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable(OverrideVariable);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public SecondVolume()
    {
        if (OverridePath is string overrideRoot)
        {
            AssertExists(overrideRoot);
            AssertDistinctVolume(overrideRoot);

            // Parallel fixtures each get their own subdirectory, which is all Dispose removes.
            _directory = CreateInstanceDirectory(overrideRoot);
            Root = _directory;
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            _subst = new SubstDrive();
            Root = _subst.Root;
            return;
        }

        _directory = CreateInstanceDirectory(ShmRoot);
        Root = _directory;
    }

    public void Dispose()
    {
        _subst?.Dispose();

        if (_directory is not null)
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leaked directory under /dev/shm is gone at reboot.
            }
        }
    }

    private static string CreateInstanceDirectory(string parent)
        => Directory
            .CreateDirectory(Path.Combine(parent, "renamer-vol-" + Guid.NewGuid().ToString("N")))
            .FullName;

    private static void AssertExists(string overrideRoot)
    {
        if (!Directory.Exists(overrideRoot))
        {
            throw new InvalidOperationException(
                $"{OverrideVariable} names '{overrideRoot}', which is not an existing directory. " +
                "Create or mount it first, or unset the variable.");
        }
    }

    // An override on the temp tree's own volume would run every gated test down the same-volume
    // path. The check uses the production classifier, so it agrees with the code under test.
    private static void AssertDistinctVolume(string overrideRoot)
    {
        string tempPath = Path.GetTempPath();
        if (!VolumeClassifier.SameVolume(overrideRoot, tempPath))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{OverrideVariable} names '{overrideRoot}', which is on the same volume as the test " +
            $"temp tree '{tempPath}' (volume key '{VolumeClassifier.VolumeKey(overrideRoot)}' == " +
            $"'{VolumeClassifier.VolumeKey(tempPath)}'). A move into it would take the atomic " +
            "same-volume path, so every cross-volume test would pass while exercising nothing. " +
            "Point the variable at a directory on a genuinely different filesystem.");
    }
}
