using WhisparrSync.State;

namespace WhisparrSync.Safety;

/// <summary>One detected overlap: a Whisparr root whose trailing segment doubles the Scene Folder Format's leading
/// literal, the doubled <see cref="Prefix"/>, and the <see cref="SuggestedRoot"/> one level above.</summary>
/// <remarks><see cref="Root"/> is reported AS SUPPLIED (un-normalized) so the user recognizes it in the advisory.</remarks>
internal sealed record SceneFolderOverlap(string Root, string Prefix, string SuggestedRoot);

/// <summary>
/// Onboarding advisory (v3/Eros): Eros builds each movie folder as <c>&lt;root folder&gt;</c> + <c>&lt;Scene
/// Folder Format output&gt;</c>. When the configured root's trailing segment equals the format's leading literal
/// segment (root <c>/data/media/scenes</c> + format <c>scenes/{…}</c>), Eros doubles the segment
/// (<c>/data/media/scenes/scenes/…</c>) and every scene lands at an unfindable path. Pure and host-free so it is
/// directly unit-testable; a sibling to <see cref="RootOverlapDetector"/> and built to the same discipline.
/// </summary>
internal static class SceneFolderOverlapDetector
{
    /// <summary>
    /// The leading LITERAL path segment of a Scene Folder Format, or <c>null</c> when the format is empty or its
    /// first segment is a <c>{token}</c> (nothing literal to double).
    /// </summary>
    public static string? LeadingLiteralSegment(string? sceneFolderFormat)
    {
        if (string.IsNullOrWhiteSpace(sceneFolderFormat))
        {
            return null;
        }

        // Eros concatenates root + format at the FIRST path boundary, so only the leading segment can collide
        // with the root's trailing segment (why the FIRST segment, not any later one). A token-leading segment
        // carries no literal, so there is nothing to advise on.
        var firstSegment = sceneFolderFormat.Replace('\\', '/').Trim().TrimStart('/').Split('/', 2)[0];
        return firstSegment.Length == 0 || firstSegment.Contains('{') ? null : firstSegment;
    }

    /// <summary>
    /// Flags every root whose trailing path segment equals the format's leading literal segment. Empty/whitespace
    /// roots are ignored; a token-leading or empty format, or a null collection, yields no overlaps.
    /// </summary>
    public static IReadOnlyList<SceneFolderOverlap> Detect(
        string? sceneFolderFormat, IReadOnlyCollection<string>? roots)
    {
        var overlaps = new List<SceneFolderOverlap>();
        if (LeadingLiteralSegment(sceneFolderFormat) is not { } literal || roots is null)
        {
            return overlaps;
        }

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var normalized = EventLedger.NormalizePath(root);
            var lastSlash = normalized.LastIndexOf('/');
            var trailing = lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;

            // Case-SENSITIVE (Ordinal): the Linux/Docker target treats /data/media/scenes and /data/media/Scenes
            // as distinct directories, matching RootOverlapDetector and EventLedger.NormalizePath (WR-01).
            if (string.Equals(trailing, literal, StringComparison.Ordinal))
            {
                var suggestedRoot = lastSlash >= 0 ? normalized[..lastSlash] : string.Empty;
                overlaps.Add(new SceneFolderOverlap(root, literal, suggestedRoot));
            }
        }

        return overlaps;
    }
}
