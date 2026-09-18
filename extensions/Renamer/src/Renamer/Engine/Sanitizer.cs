using System.Globalization;
using System.Text;
using Renamer.Options;

namespace Renamer.Engine;

// Cleans one path segment at a time. '/' is treated as an ordinary illegal char here, because the
// engine splits the folder template on '/' before calling in and rejoins afterwards.
public static class Sanitizer
{
    // Illegal in a Windows filename. Control chars are handled separately.
    private static readonly char[] Illegal = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    private static readonly char[] TrimEdge = { ' ', '.' };

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Order: drop control chars, strip or replace the illegal set per IllegalReplacement, replace
    // spaces per SpaceReplacement, collapse runs of the active separator, then trim leading and
    // trailing separators, spaces and dots.
    public static string CleanSegment(string s, RenamerOptions o)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            // A removed char goes before the illegal and space checks, so a char that is both in the
            // remove set and illegal disappears rather than becoming IllegalReplacement.
            if (o.RemoveCharacters.Length > 0 && o.RemoveCharacters.Contains(ch))
            {
                continue;
            }

            if (char.IsControl(ch))
            {
                continue;
            }

            if (Array.IndexOf(Illegal, ch) >= 0)
            {
                if (o.IllegalReplacement is { Length: > 0 } r)
                {
                    sb.Append(r);
                }

                // An empty IllegalReplacement strips the char.
                continue;
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
            // Windows refuses a reserved device name whatever the extension: CON and CON.mkv both
            // resolve to the device, so a same-name title needs disambiguating or the move fails.
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

    // Spaces always collapse. A configured space-replacement token collapses as well, including a
    // multi-char one.
    private static string CollapseRuns(string s, RenamerOptions o)
    {
        var spaceRepl = o.SpaceReplacement;
        if (spaceRepl is { Length: > 0 })
        {
            string doubled = spaceRepl + spaceRepl;
            while (s.Contains(doubled))
            {
                s = s.Replace(doubled, spaceRepl);
            }
        }

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

    // Trims leading and trailing spaces, dots and any configured space-replacement token.
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

    // The invariant culture is used throughout, so the transform does not vary by host locale.
    public static string ApplyCase(string s, CaseTransform c) => c switch
    {
        CaseTransform.Lower => s.ToLowerInvariant(),
        CaseTransform.Title => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant()),
        _ => s,
    };

    // Folds Latin diacritics to their base letter. It does not romanize non-Latin scripts: a
    // Cyrillic, Kanji or Arabic string has no diacritics to fold and comes back unchanged. A caller
    // that then stripped surviving non-ASCII would empty those titles.
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

    // Folds typographic punctuation to ASCII and leaves every other character alone. Accented
    // letters are Transliterate's job.
    public static string NormalizePunctuation(string s)
    {
        // Scrapers store smart quotes and dashes in metadata while the files on disk are plain ASCII.
        // Folding the punctuation back keeps those straight-quote files as no-ops instead of moves.
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
