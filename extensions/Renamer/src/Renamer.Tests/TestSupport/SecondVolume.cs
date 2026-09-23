using Renamer.Planner;

namespace Renamer.Tests.TestSupport;

// A directory on a filesystem genuinely different from the temp tree's, so a move into it is a real
// cross-volume move and VolumeClassifier classifies it as one. Use this rather than SubstDrive
// directly: subst is Windows-only, and a test that reaches for it unconditionally either fails or
// quietly does nothing everywhere else. Three arms, in precedence order - an explicit choice
// outranks an inferred one on every OS: COVE_TEST_SECOND_VOLUME, naming an existing directory on
// another filesystem. This is the only arm available on macOS, where neither of the others applies.
// Windows - a SubstDrive, a second path root, which is what GetPathRoot(string) keys on there. Unix
// - a directory under /dev/shm, a tmpfs that is a distinct entry in the kernel mount table and
// therefore a distinct volume key; measured present and writable with no privilege in a Linux
// container and on the CI runner. That matters because CI runs Linux: before this existed, every
// cross-volume execution proof was unreachable there, and seven of them returned early and reported
// pass while executing nothing. A misconfigured override is refused rather than absorbed: pointing
// the variable at the temp tree's own volume would leave every gated test running, passing, and
// proving nothing - the same silent-no-op this fixture was written to end. See
// AssertDistinctVolume. This is not a second physical disk. It is a second filesystem, which is
// what the classifier and the copy-verify-delete path actually branch on. A true two-drive run
// stays a manual check.
public sealed class SecondVolume : IDisposable
{
    private const string ShmRoot = "/dev/shm";

    // The environment variable naming a directory on a second filesystem.
    public const string OverrideVariable = "COVE_TEST_SECOND_VOLUME";

    // The root to place a cross-volume destination under.
    public string Root { get; }

    private readonly SubstDrive? _subst;
    private readonly string? _directory;

    // Whether this host can supply a second filesystem at all. False on a Unix without /dev/shm and
    // no override - notably macOS, where a cross-volume test cannot run and must say so rather than
    // pass.
    public static bool IsAvailable =>
        OverridePath is string p && Directory.Exists(p)
        || OperatingSystem.IsWindows()
        || Directory.Exists(ShmRoot);

    // Reason string for the Skip call that guards an unavailable host.
    public const string UnavailableReason =
        "needs a second filesystem: " + OverrideVariable +
        " naming a directory on one, a subst drive on Windows, or /dev/shm on Unix";

    // The configured override, or null when the variable is unset or blank.
    private static string? OverridePath
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable(OverrideVariable);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public SecondVolume()
        : this(OverridePath)
    {
    }

    // The testable seam. overridePath is passed explicitly by the fixture's own tests rather than
    // set in the environment, because a process-global variable cannot be mutated safely while
    // xUnit runs test classes in parallel.
    internal SecondVolume(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            string overrideRoot = overridePath.Trim();
            AssertExists(overrideRoot);
            AssertDistinctVolume(overrideRoot);

            // A per-instance subdir, mirroring the /dev/shm arm: parallel fixtures must not share one
            // directory, and Dispose must have something of its own to remove.
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
                // Only the subdir this instance created - never a caller-supplied override directory,
                // which on a real machine is a mount point holding somebody else's data.
                Directory.Delete(_directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup; /dev/shm is tmpfs, so a leaked dir dies with the machine.
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
                "Create or mount it first (see Renamer.Tests/README.md), or unset the variable to " +
                "fall back to the inferred arm.");
        }
    }

    // Refuses an override that resolves to the same volume as the temp tree the cross-volume tests
    // move from, naming both sides so the misconfiguration is actionable from CI output alone. The
    // decision is delegated to SameVolume - the same classifier the gated tests themselves key on -
    // so this check and those tests cannot disagree about what "cross-volume" means. Throwing is
    // deliberate: the alternative, quietly using the directory anyway, keeps roughly a dozen
    // copy/verify/delete proofs green while they exercise the atomic same-volume path instead.
    private static void AssertDistinctVolume(string overrideRoot)
    {
        string tempPath = Path.GetTempPath();
        if (!VolumeClassifier.SameVolume(overrideRoot, tempPath))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{OverrideVariable} names '{overrideRoot}', which is on the SAME volume as the test " +
            $"temp tree '{tempPath}' (volume key '{VolumeClassifier.VolumeKey(overrideRoot)}' == " +
            $"'{VolumeClassifier.VolumeKey(tempPath)}'). A move into it would take the atomic " +
            "same-volume path, so every cross-volume test would pass while exercising nothing. " +
            "Point the variable at a directory on a genuinely different filesystem.");
    }
}
