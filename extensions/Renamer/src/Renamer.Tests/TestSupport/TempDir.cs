namespace Renamer.Tests.TestSupport;

/// <summary>
/// A per-test, isolated, auto-cleaned real directory under <see cref="Path.GetTempPath"/> —
/// the real-filesystem tier for move/lock/sidecar tests (deterministic temp-directory disk tests,
/// rather than mocking the filesystem). Create one per test (or per fixture) and dispose to remove it.
/// </summary>
public sealed class TempDir : IDisposable
{
    /// <summary>The absolute root of this temp directory (created in the constructor).</summary>
    public string Root { get; } = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "renamer-test-" + Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>
    /// Creates a file at <paramref name="relativePath"/> under <see cref="Root"/> (creating any
    /// intermediate directories) and writes <paramref name="content"/> to it. Returns the file's
    /// absolute path.
    /// </summary>
    public string Touch(string relativePath, string content = "x")
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Recursively deletes <see cref="Root"/>, swallowing any cleanup failures.</summary>
    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch { /* best-effort cleanup; a leaked temp dir is harmless */ }
    }
}
