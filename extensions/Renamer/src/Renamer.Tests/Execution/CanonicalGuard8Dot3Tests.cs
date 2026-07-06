using System.Runtime.InteropServices;
using Renamer.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// GATE-03 8.3 short-name aliasing: a destination expressed through a short name (e.g. <c>PROGRA~1</c>)
/// must resolve to its canonical LONG form before the containment compare, so a short-name path that
/// is genuinely under the allowed root is treated consistently with its long form (and a short-name
/// path keyed to dodge a long-form allowlist cannot slip through). <see cref="Path.GetFullPath(string)"/>
/// does NOT expand short names — only the guard's <c>kernel32!GetLongPathNameW</c> step does.
/// Exercised against the real filesystem via <see cref="TempDir"/>.
/// </summary>
[Trait("Tier", "Integration")]
[Trait("Adversarial", "ShortName")]
public sealed class CanonicalGuard8Dot3Tests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string lpszLongPath, char[] lpszShortPath, uint cchBuffer);

    /// <summary>Returns the 8.3 short form of <paramref name="longPath"/>, or null when the volume has no short alias for it.</summary>
    private static string? GetShortPath(string longPath)
    {
        var buffer = new char[short.MaxValue];
        uint len = GetShortPathNameW(longPath, buffer, (uint)buffer.Length);
        return len > 0 && len < buffer.Length ? new string(buffer, 0, (int)len) : null;
    }

    [SkippableFact]
    public void ShortNameDestinationUnderRoot_ResolvesToLongForm_IsAccepted()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "8.3 short-name expansion is Windows-only");

        using var dir = new TempDir();
        string allowed = Directory.CreateDirectory(Path.Combine(dir.Root, "allowed")).FullName;
        // A directory whose name is long enough to get a distinct 8.3 alias on a short-name-enabled volume.
        string longNamed = Directory.CreateDirectory(
            Path.Combine(allowed, "A Very Long Directory Name 2026")).FullName;

        string? shortForm = GetShortPath(longNamed);

        // If 8.3 generation is disabled on the temp volume the alias equals the long form (or is null);
        // skip WITH a visible reason rather than asserting a non-existent behavior.
        Skip.If(shortForm is null
            || string.Equals(shortForm, longNamed, StringComparison.OrdinalIgnoreCase),
            "no distinct 8.3 short alias on this volume (8dot3name likely disabled)");

        // The short-name destination is genuinely under the allowed root; the guard must expand it to
        // the canonical long form and ACCEPT it, proving the short name is not a blind spot.
        var r = CanonicalPathGuard.Check((shortForm + "/file.mkv").Replace('\\', '/'), [allowed]);

        Assert.True(r.Accepted, r.Reason);
        // The resolved target is the LONG form, not the PROGRA~1-style alias.
        Assert.DoesNotContain("~", r.ResolvedTarget);
    }
}
