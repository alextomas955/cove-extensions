using System.Text.Json;

namespace Cove.Extensions.Shared.Testing;

/// <summary>
/// The read/normalize/update half of a wire-snapshot check, shared by both extensions' snapshot tests.
/// </summary>
/// <remarks>
/// <para>
/// It deliberately does NOT assert. This assembly carries no xUnit reference — the same reason
/// <see cref="TierTraitGuard"/> reads attributes by type name — so the caller owns the assertion and its
/// failure message, which is also where the extension-specific wording belongs.
/// </para>
/// <para>
/// What is shared is the part that was identical in both suites and has no reason to differ: re-serializing
/// the compact JSON through a <see cref="JsonDocument"/> so only whitespace changes (element order is
/// preserved, so a reordered payload still fails), resolving the fixture beside the calling test file, and
/// honouring <c>WIRE_SNAPSHOT_UPDATE=1</c> as the one way a frozen fixture is rewritten.
/// </para>
/// </remarks>
public static class WireSnapshot
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The outcome of resolving a snapshot: what to compare, or that the update mode wrote it.</summary>
    /// <param name="Wrote">
    /// True when <c>WIRE_SNAPSHOT_UPDATE=1</c> rewrote the fixture, in which case there is nothing to assert.
    /// </param>
    /// <param name="Exists">Whether the fixture file is present; false is a caller-side failure, not an exception.</param>
    /// <param name="Path">The resolved fixture path, for the caller's failure message.</param>
    /// <param name="Expected">The stored fixture text, newline-normalized. Empty when <paramref name="Exists"/> is false.</param>
    /// <param name="Actual">The re-serialized payload, newline-normalized.</param>
    public sealed record Result(bool Wrote, bool Exists, string Path, string Expected, string Actual);

    /// <summary>
    /// Normalizes <paramref name="actualCompactJson"/>, resolves the fixture <paramref name="name"/> under a
    /// <c>fixtures</c> directory beside <paramref name="callerPath"/>, and either rewrites it (update mode) or
    /// returns both sides for the caller to compare.
    /// </summary>
    /// <exception cref="JsonException"><paramref name="actualCompactJson"/> is not valid JSON.</exception>
    public static Result Resolve(string actualCompactJson, string name, string callerPath)
    {
        using var doc = JsonDocument.Parse(actualCompactJson);
        string actual = JsonSerializer.Serialize(doc.RootElement, Indented);

        var dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(callerPath)!, "fixtures");
        var path = System.IO.Path.Combine(dir, name + ".json");

        if (Environment.GetEnvironmentVariable("WIRE_SNAPSHOT_UPDATE") == "1")
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, actual);
            return new Result(Wrote: true, Exists: true, path, string.Empty, string.Empty);
        }

        if (!File.Exists(path))
        {
            return new Result(Wrote: false, Exists: false, path, string.Empty, actual.ReplaceLineEndings("\n"));
        }

        return new Result(
            Wrote: false,
            Exists: true,
            path,
            File.ReadAllText(path).ReplaceLineEndings("\n"),
            actual.ReplaceLineEndings("\n"));
    }
}
