using System.Globalization;
using System.Text;
using Renamer.Options;

namespace Renamer.Engine;

/// <summary>
/// Pure, static filename-segment sanitization + case/transliteration transforms.
/// Operates on ONE path segment at a time —
/// it does NOT special-case <c>/</c> beyond the illegal set, because the engine splits the
/// folder template on <c>/</c>, cleans each segment, then rejoins (keeping <c>/</c> only
/// as the path separator). No I/O, no host types.
/// </summary>
public static class Sanitizer
{
    /// <summary>The Windows-illegal filename character set. Control chars are handled separately.</summary>
    private static readonly char[] Illegal = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    /// <summary>Chars trimmed from the leading/trailing edges of a cleaned segment.</summary>
    private static readonly char[] TrimEdge = { ' ', '.' };

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Cleans a single path segment: drops control chars, strips-or-replaces the illegal
    /// set per <see cref="RenamerOptions.IllegalReplacement"/>, replaces spaces per
    /// <see cref="RenamerOptions.SpaceReplacement"/>, collapses runs of the active
    /// separator/space chars, then trims leading/trailing separators, spaces, and dots.
    /// </summary>
    public static string CleanSegment(string s, RenamerOptions o)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            // A removed char is dropped before the illegal/space checks, so a char that is both in
            // the remove-set and OS-illegal is simply gone rather than first becoming IllegalReplacement.
            if (o.RemoveCharacters.Length > 0 && o.RemoveCharacters.Contains(ch))
            {
                continue;
            }

            if (char.IsControl(ch))
            {
                continue; // strip control chars (always)
            }

            if (Array.IndexOf(Illegal, ch) >= 0)
            {
                if (o.IllegalReplacement is { Length: > 0 } r)
                {
                    sb.Append(r);
                }

                continue; // default: strip
            }

            if (ch == ' ' && o.SpaceReplacement is { Length: > 0 } sr)
            {
                sb.Append(sr);
                continue;
            }

            sb.Append(ch);
        }

        var collapsed = CollapseRuns(sb.ToString(), o);
        var trimmed = TrimEdges(collapsed, o);

        if (IsReservedDeviceName(trimmed))
        {
            // Windows refuses a reserved device name regardless of extension (CON, CON.mkv both
            // resolve to the device), so a same-name title must be disambiguated or the OS move fails.
            int dot = trimmed.IndexOf('.');
            return dot < 0 ? trimmed + "_" : trimmed.Insert(dot, "_");
        }

        return trimmed;
    }

    private static bool IsReservedDeviceName(string segment)
    {
        int dot = segment.IndexOf('.');
        var stem = dot < 0 ? segment : segment.Substring(0, dot);
        return stem.Length > 0 && ReservedDeviceNames.Contains(stem);
    }

    /// <summary>
    /// Collapses consecutive runs of a "separator" token (a space, or the configured
    /// space-replacement string) down to a single occurrence.
    /// </summary>
    private static string CollapseRuns(string s, RenamerOptions o)
    {
        // Spaces always collapse. When a multi-char space-replacement is configured,
        // collapse repeated occurrences of that exact token too.
        var spaceRepl = o.SpaceReplacement;
        if (spaceRepl is { Length: > 0 })
        {
            // Collapse repeated replacement tokens (e.g. "_ _ _" -> "_") to a single token.
            string doubled = spaceRepl + spaceRepl;
            while (s.Contains(doubled))
            {
                s = s.Replace(doubled, spaceRepl);
            }
        }

        // Collapse runs of literal spaces.
        if (s.Contains("  "))
        {
            var sb = new StringBuilder(s.Length);
            bool prevSpace = false;
            foreach (var ch in s)
            {
                if (ch == ' ')
                {
                    if (prevSpace)
                    {
                        continue;
                    }

                    prevSpace = true;
                }
                else
                {
                    prevSpace = false;
                }
                sb.Append(ch);
            }
            s = sb.ToString();
        }

        return s;
    }

    /// <summary>
    /// Trims leading/trailing spaces, dots, and any configured space-replacement token
    /// from a cleaned segment.
    /// </summary>
    private static string TrimEdges(string s, RenamerOptions o)
    {
        var spaceRepl = o.SpaceReplacement;
        if (spaceRepl is { Length: > 0 })
        {
            while (s.StartsWith(spaceRepl, StringComparison.Ordinal))
            {
                s = s.Substring(spaceRepl.Length);
            }

            while (s.EndsWith(spaceRepl, StringComparison.Ordinal))
            {
                s = s.Substring(0, s.Length - spaceRepl.Length);
            }
        }

        return s.Trim(TrimEdge);
    }

    /// <summary>
    /// Applies the configured case transform: <see cref="CaseTransform.None"/> is identity,
    /// <see cref="CaseTransform.Lower"/> uses <c>ToLowerInvariant</c>, <see cref="CaseTransform.Title"/>
    /// uses <c>InvariantCulture.TextInfo.ToTitleCase</c>.
    /// </summary>
    public static string ApplyCase(string s, CaseTransform c) => c switch
    {
        CaseTransform.Lower => s.ToLowerInvariant(),
        CaseTransform.Title => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant()),
        _ => s,
    };

    /// <summary>
    /// Folds Latin diacritics to their base letter (e.g. <c>é</c>→<c>e</c>, <c>ñ</c>→<c>n</c>) via
    /// Unicode decomposition (<see cref="NormalizationForm.FormD"/>) + stripping
    /// <see cref="UnicodeCategory.NonSpacingMark"/> chars, then re-composing.
    /// CAVEAT: this folds diacritics ONLY — it does not romanize non-Latin scripts.
    /// A Cyrillic/Kanji/Arabic string has no diacritics to fold and survives non-empty;
    /// callers MUST NOT additionally strip surviving non-ASCII (that would empty those titles).
    /// </summary>
    public static string Transliterate(string s)
    {
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Folds a small, punctuation-only set of typographic characters to their ASCII equivalents:
    /// curly single quotes (U+2018/U+2019) → <c>'</c>, curly double quotes (U+201C/U+201D) → <c>"</c>,
    /// en/em dashes (U+2013/U+2014) → <c>-</c>, and the ellipsis (U+2026) → three ASCII dots. Every
    /// other character (letters, diacritics, non-Latin scripts) is left verbatim — folding accented
    /// letters is <see cref="Transliterate"/>'s job, not this method's.
    /// </summary>
    public static string NormalizePunctuation(string s)
    {
        // Scrapers store smart quotes/dashes in metadata while the files on disk are plain ASCII;
        // folding punctuation back to ASCII keeps those straight-quote files as no-ops instead of moves.
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '‘':
                case '’':
                    sb.Append('\'');
                    break;
                case '“':
                case '”':
                    sb.Append('"');
                    break;
                case '–':
                case '—':
                    sb.Append('-');
                    break;
                case '…':
                    sb.Append("...");
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        return sb.ToString();
    }
}
