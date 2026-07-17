using System.Diagnostics;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// A Windows-only test fixture that maps a free drive letter to a real backing directory via
/// <c>subst</c>, giving a SECOND path root that resolves to the SAME physical volume. This lets the
/// executor's <c>VolumeClassifier</c> branch report a cross-volume move (distinct
/// <see cref="Path.GetPathRoot(string)"/> values) and exercise the real <c>CrossVolumeMover</c>
/// end-to-end on one machine — no second physical
/// drive required (a real two-drive run remains a manual cross-platform check).
///
/// The backing directory is created under the temp tree; both it and the subst mapping are torn down
/// on dispose. A free drive letter is probed at construction so parallel tests do not collide.
/// </summary>
public sealed class SubstDrive : IDisposable
{
    /// <summary>The mapped drive root, e.g. <c>"P:\"</c> — a distinct path root from the temp dir.</summary>
    public string Root { get; }

    private readonly char _letter;
    private readonly string _backing;
    private bool _mapped;

    public SubstDrive()
    {
        _backing = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "renamer-subst-" + Guid.NewGuid().ToString("N"))).FullName;

        // Probe-then-subst is a TOCTOU race: with multiple SubstDrive instances constructing in
        // parallel (xUnit runs test classes concurrently), two can pick the same "free" letter between
        // the probe and the subst call, and the loser's `subst` fails with a drive-already-mapped error.
        // Re-probe and retry on failure across the candidate range so a collision just moves on to the
        // next free letter instead of failing the test.
        _letter = MapFreeDriveWithRetry(_backing);
        _mapped = true;
        Root = _letter + ":\\";
    }

    public void Dispose()
    {
        if (_mapped)
        {
            try { Run($"{_letter}: /d"); }
            catch { /* best-effort unmap */ }
            _mapped = false;
        }

        try { Directory.Delete(_backing, recursive: true); }
        catch { /* best-effort cleanup; a leaked temp dir is harmless */ }
    }

    /// <summary>
    /// Maps a free drive letter to <paramref name="backing"/>, re-probing and retrying across the
    /// candidate range on a subst failure so a parallel-construction collision (the probed letter got
    /// claimed by another instance before our subst ran) just advances to the next free letter. Returns
    /// the letter actually mapped.
    /// </summary>
    private static char MapFreeDriveWithRetry(string backing)
    {
        var tried = new HashSet<char>();
        Exception? last = null;

        // Bounded by the candidate range (P..Z); each iteration re-probes so a letter another instance
        // just claimed is excluded on the next pass.
        for (int attempt = 0; attempt < 11; attempt++)
        {
            char? candidate = FindFreeDriveLetter(tried);
            if (candidate is null)
            {
                break;
            }

            tried.Add(candidate.Value);
            try
            {
                Run($"{candidate.Value}: \"{backing}\"");
                return candidate.Value;
            }
            catch (Exception ex)
            {
                // The letter was taken between the probe and the subst (or otherwise failed); try the next.
                last = ex;
            }
        }

        throw new InvalidOperationException("No free drive letter available for subst.", last);
    }

    private static char? FindFreeDriveLetter(HashSet<char> exclude)
    {
        var used = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();

        // Probe high letters first to avoid clashing with real volumes.
        for (char c = 'Z'; c >= 'P'; c--)
        {
            if (!used.Contains(c) && !exclude.Contains(c))
            {
                return c;
            }
        }

        return null;
    }

    private static void Run(string args)
    {
        var psi = new ProcessStartInfo("subst", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"subst {args} failed (exit {p.ExitCode}): {stderr}");
        }
    }
}
