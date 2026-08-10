using Renamer.Execution;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// A directory on a filesystem genuinely different from the temp tree's, so a move into it is a real
/// cross-volume move and <see cref="VolumeClassifier"/> classifies it as one. Use
/// this rather than <see cref="SubstDrive"/> directly: subst is Windows-only, and a test that reaches
/// for it unconditionally either fails or quietly does nothing everywhere else.
/// </summary>
/// <remarks>
/// Windows takes a <see cref="SubstDrive"/> — a second path root, which is what
/// <see cref="Path.GetPathRoot(string)"/> keys on there. Unix takes a directory under
/// <c>/dev/shm</c>, a tmpfs that is a distinct entry in the kernel mount table and therefore a
/// distinct volume key; measured present and writable with no privilege in a Linux container and on
/// the CI runner. That matters because CI runs Linux: before this existed, every cross-volume
/// execution proof was unreachable there, and seven of them returned early and reported PASS while
/// executing nothing.
/// <para>
/// This is not a second physical disk. It is a second FILESYSTEM, which is what the classifier and
/// the copy-verify-delete path actually branch on. A true two-drive run stays a manual check.
/// </para>
/// </remarks>
public sealed class SecondVolume : IDisposable
{
    private const string ShmRoot = "/dev/shm";

    /// <summary>The root to place a cross-volume destination under.</summary>
    public string Root { get; }

    private readonly SubstDrive? _subst;
    private readonly string? _directory;

    /// <summary>
    /// Whether this host can supply a second filesystem at all. False only on a Unix without
    /// <c>/dev/shm</c>, where a cross-volume test cannot run and must say so rather than pass.
    /// </summary>
    public static bool IsAvailable => OperatingSystem.IsWindows() || Directory.Exists(ShmRoot);

    /// <summary>Reason string for the <c>Skip</c> call that guards an unavailable host.</summary>
    public const string UnavailableReason =
        "needs a second filesystem: a subst drive on Windows, or /dev/shm on Unix";

    public SecondVolume()
    {
        if (OperatingSystem.IsWindows())
        {
            _subst = new SubstDrive();
            Root = _subst.Root;
            return;
        }

        _directory = Directory
            .CreateDirectory(Path.Combine(ShmRoot, "renamer-vol-" + Guid.NewGuid().ToString("N")))
            .FullName;
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
            catch
            {
                // Best-effort cleanup; /dev/shm is tmpfs, so a leaked dir dies with the machine.
            }
        }
    }
}
