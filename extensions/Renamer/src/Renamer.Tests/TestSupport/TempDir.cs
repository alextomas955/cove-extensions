namespace Renamer.Tests.TestSupport;

// A per-test, isolated, auto-cleaned real directory under GetTempPath: the real-filesystem tier for
// the move, lock and sidecar tests, rather than a mocked filesystem.
public sealed class TempDir : IDisposable
{
    public string Root { get; } = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "renamer-test-" + Guid.NewGuid().ToString("N"))).FullName;

    public string Touch(string relativePath, string content = "x")
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort cleanup; a leaked temp dir is harmless */ }
    }
}
