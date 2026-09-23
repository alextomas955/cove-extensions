namespace Renamer.Tests.TestSupport;

// A per-test, isolated, auto-cleaned real directory under GetTempPath - the real-filesystem tier
// for move/lock/sidecar tests (deterministic temp-directory disk tests, rather than mocking the
// filesystem). Create one per test (or per fixture) and dispose to remove it.
public sealed class TempDir : IDisposable
{
    // The absolute root of this temp directory (created in the constructor).
    public string Root { get; } = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "renamer-test-" + Guid.NewGuid().ToString("N"))).FullName;

    // Creates a file at relativePath under Root (creating any intermediate directories) and writes
    // content to it. Returns the file's absolute path.
    public string Touch(string relativePath, string content = "x")
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    // Recursively deletes Root, swallowing any cleanup failures.
    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort cleanup; a leaked temp dir is harmless */ }
    }
}
