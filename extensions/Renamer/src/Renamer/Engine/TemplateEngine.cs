using System.Text;
using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Engine;

// Canonical token spellings shared with the metadata projector and the UI. Token lookups are
// case-insensitive.
public static class Tokens
{
    public const string Title = "title";
    public const string Studio = "studio";
    public const string ParentStudio = "parentStudio";
    public const string StudioCode = "studioCode";
    public const string Director = "director";
    public const string Bitrate = "bitrate";
    public const string Date = "date";
    public const string Year = "year";
    public const string Height = "height";
    public const string Width = "width";
    public const string Resolution = "resolution";
    public const string VideoCodec = "videoCodec";
    public const string AudioCodec = "audioCodec";
    public const string FrameRate = "frameRate";
    public const string Duration = "duration";
    public const string Performers = "performers";
    public const string Tags = "tags";
    public const string Ext = "ext";
}

// Evaluation order: (1) build the resolved token map (scalars, multi-value performers/tags,
// derived $resolution); (2) render the filename and folder templates independently, collapsing
// {} spans whose every inner token resolved empty; (3) apply case and transliteration transforms;
// (4) sanitize per segment (filename as one segment so '/' is stripped; folder split on '/', each
// piece cleaned, rejoined with '/'); (5) resolve the extension; (6) length-fit.
//
// The engine is pure: no Path, File or database access. Path-traversal confinement ('..',
// absolute paths) belongs to the executor, because the engine never sees the library root.
public static class TemplateEngine
{
    public static RenamerResult Render(
        IReadOnlyDictionary<string, string> tokens,
        IReadOnlyDictionary<string, IReadOnlyList<string>> multiValues,
        RenamerOptions options,
        Action<string>? logUnbalanced = null,
        IReadOnlyList<RenamerPerformer>? performers = null,
        IReadOnlyList<(int Id, string Name)>? tags = null)
        => RenderWithDropped(tokens, multiValues, options, logUnbalanced, performers, tags).result;

    // Also returns the DropOrder fields the length reducer dropped to make the name fit, as
    // reported by LengthReducer itself. Render delegates here, so there is one rendering path.
    public static (RenamerResult result, IReadOnlyList<string> dropped) RenderWithDropped(
        IReadOnlyDictionary<string, string> tokens,
        IReadOnlyDictionary<string, IReadOnlyList<string>> multiValues,
        RenamerOptions options,
        Action<string>? logUnbalanced = null,
        IReadOnlyList<RenamerPerformer>? performers = null,
        IReadOnlyList<(int Id, string Name)>? tags = null)
    {
        var resolved = BuildResolvedMap(tokens, multiValues, options, performers, tags);

        // Resolved up front so a template referencing $ext does not emit it twice: $ext resolves
        // empty during the filename render, and the extension is appended as RenamerResult.Ext.
        string ext = NormalizeExt(Resolve(resolved, Tokens.Ext));

        string filename = RenderFilename(options.FilenameTemplate, resolved, options, logUnbalanced);

        string folder = RenderFolder(options.FolderTemplate, resolved, options, logUnbalanced);

        return LengthReducer.FitWithDropped(
            folder, filename, ext, options,
            // Re-render with the cumulative set of dropped fields forced empty.
            droppedFields =>
            {
                var reduced = new Dictionary<string, string>(resolved, StringComparer.OrdinalIgnoreCase);
                foreach (var f in droppedFields)
                {
                    reduced[f] = string.Empty;
                }

                return (
                    RenderFolder(options.FolderTemplate, reduced, options, null),
                    RenderFilename(options.FilenameTemplate, reduced, options, null));
            });
    }

    // Copies the caller's scalar tokens, overrides $performers and $tags with the joined
    // multi-value resolution, and derives $resolution from the dimension tokens when present.
    private static Dictionary<string, string> BuildResolvedMap(
        IReadOnlyDictionary<string, string> tokens,
        IReadOnlyDictionary<string, IReadOnlyList<string>> multiValues,
        RenamerOptions options,
        IReadOnlyList<RenamerPerformer>? performerRecords = null,
        IReadOnlyList<(int Id, string Name)>? tagRefs = null)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in tokens)
        {
            map[kv.Key] = kv.Value;
        }

        // Field rewrites run before the multi-value overrides and the render. The keys are
        // materialized so the dictionary is not mutated while enumerating it.
        foreach (var key in map.Keys.ToList())
        {
            map[key] = FieldRewriter.RewriteScalar(key, map[key], options);
        }

        if (TryGetMulti(multiValues, Tokens.Performers, out var performers))
        {
            // Performers already named in the resolved title are dropped before MultiValue.Resolve
            // applies MaxCount, so a dropped name frees an overflow slot.
            string resolvedTitle = map.TryGetValue(Tokens.Title, out var t) ? t : string.Empty;

            if (performerRecords is not null)
            {
                // Filtering the records preserves per-position multiplicity, so when two performers
                // share a name only the matching positions drop and a surviving duplicate is kept.
                var survivors = FieldRewriter.DropPerformersInTitle(performerRecords, resolvedTitle, options);
                map[Tokens.Performers] = MultiValue.Resolve(survivors, options.Performers);
            }
            else
            {
                performers = FieldRewriter.DropPerformersInTitle(performers, resolvedTitle, options);
                map[Tokens.Performers] = MultiValue.Resolve(performers, options.Performers);
            }
        }

        // The id pairs take precedence: the tag whitelist and blacklist match on ids, so a name-only
        // list can be sorted and joined but not filtered.
        if (tagRefs is not null)
        {
            map[Tokens.Tags] = MultiValue.Resolve(tagRefs, options.Tags);
        }
        else if (TryGetMulti(multiValues, Tokens.Tags, out var tags))
        {
            map[Tokens.Tags] = MultiValue.Resolve(tags, options.Tags);
        }

        // A caller-supplied $resolution wins over the derived one. Deriving it needs both dimensions,
        // as Cove's own badge does, and Cove stores an unknown dimension as 0, so a non-positive one
        // is an absent one. With either missing the token stays out of the map, so its group drops.
        if (!map.ContainsKey(Tokens.Resolution)
            && map.TryGetValue(Tokens.Width, out var w)
            && int.TryParse(w, out var width)
            && width > 0
            && map.TryGetValue(Tokens.Height, out var h)
            && int.TryParse(h, out var height)
            && height > 0)
        {
            map[Tokens.Resolution] = ResolutionLabel.FromDimensions(width, height);
        }

        return map;
    }

    private static bool TryGetMulti(
        IReadOnlyDictionary<string, IReadOnlyList<string>> multiValues,
        string key,
        out IReadOnlyList<string> values)
    {
        foreach (var kv in multiValues)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                values = kv.Value;
                return true;
            }
        }
        values = Array.Empty<string>();
        return false;
    }

    // Case-insensitive token lookup. An unknown or absent token resolves to empty.
    private static string Resolve(IReadOnlyDictionary<string, string> resolved, string name)
        => resolved.TryGetValue(name, out var v) ? v ?? string.Empty : string.Empty;

    // Matches the bare $resolution token the engine supports; there is no ${...} form. The match is
    // case-insensitive and only counts when the next char is not a token-name char, so $resolutionx
    // does not match.
    private static bool TemplateRendersResolution(string template)
    {
        const string tok = "$" + Tokens.Resolution;
        int from = 0;
        while (from <= template.Length - tok.Length)
        {
            int idx = template.IndexOf(tok, from, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return false;
            }

            int end = idx + tok.Length;
            char after = end < template.Length ? template[end] : '\0';
            if (!(char.IsLetterOrDigit(after) || after == '_'))
            {
                return true;
            }

            from = idx + 1;
        }

        return false;
    }

    // Removes one trailing resolution tag: a bracketed ResolutionLabel.KnownLabels entry such as
    // [1080p] or [4K], or an arbitrary progressive-scan label such as [368p], which an imported title
    // can carry at a height no bucket is named for. Only a tag at the end is removed, so a resolution
    // named mid-title stays.
    private static string StripTrailingResolutionTag(string value)
    {
        string trimmed = value.TrimEnd();

        // Case-insensitive, so a title carrying an older spelling of a label still matches.
        foreach (var label in ResolutionLabel.KnownLabels)
        {
            string tag = "[" + label + "]";
            if (trimmed.EndsWith(tag, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[..^tag.Length].TrimEnd();
            }
        }

        // A progressive-scan tag the fixed list does not carry, such as "[368p]".
        if (trimmed.EndsWith(']') && TryStripTrailingNumericResTag(trimmed, out var stripped))
        {
            return stripped.TrimEnd();
        }

        return value;
    }

    // Strips a trailing bracketed tag of one or more ASCII digits followed by 'p'. The caller passes a
    // string already ending in ']'. A bracketed number with no 'p', such as [28], is a serial or index
    // and is left alone.
    private static bool TryStripTrailingNumericResTag(string s, out string stripped)
    {
        stripped = s;
        int close = s.Length - 1;
        if (close < 3)
        {
            return false;
        }

        int i = close - 1;
        if (s[i] is not ('p' or 'P'))
        {
            return false;
        }

        i--;
        int digitsEnd = i;
        while (i >= 0 && char.IsAsciiDigit(s[i]))
        {
            i--;
        }

        if (i == digitsEnd || i < 0 || s[i] != '[')
        {
            return false;
        }

        stripped = s[..i];
        return true;
    }

    // Resolves one token against the same map the renderer uses, so the required-field gate sees
    // what a render would produce. An unknown or absent token resolves to empty.
    public static string ResolveField(
        IReadOnlyDictionary<string, string> tokens,
        IReadOnlyDictionary<string, IReadOnlyList<string>> multiValues,
        RenamerOptions options,
        string field)
    {
        var resolved = BuildResolvedMap(tokens, multiValues, options);
        return Resolve(resolved, field);
    }

    // Reports whether the sanitize step changes the rendered filename, by running the same render,
    // transform and clean steps the engine uses.
    public static bool WouldSanitizeFilename(
        IReadOnlyDictionary<string, string> tokens,
        IReadOnlyDictionary<string, IReadOnlyList<string>> multiValues,
        RenamerOptions options,
        IReadOnlyList<RenamerPerformer>? performers = null,
        IReadOnlyList<(int Id, string Name)>? tags = null)
    {
        var resolved = BuildResolvedMap(tokens, multiValues, options, performers, tags);
        string raw = RenderRaw(
            options.FilenameTemplate,
            WithDeDupedTitle(resolved, options.FilenameTemplate),
            suppressExt: true,
            null);
        raw = ApplyTransforms(raw, options);
        return Sanitizer.CleanSegment(raw, options) != raw;
    }

    // A render removes the title's own trailing resolution tag only where it writes a label of its
    // own, so the title keeps its tag when the template omits $resolution, when no label was derived
    // and when the length reducer dropped the field. The map is read-only because the filename and
    // folder templates render from it independently.
    private static IReadOnlyDictionary<string, string> WithDeDupedTitle(
        IReadOnlyDictionary<string, string> resolved,
        string template)
    {
        if (!TemplateRendersResolution(template)
            || Resolve(resolved, Tokens.Resolution).Length == 0
            || !resolved.TryGetValue(Tokens.Title, out var title))
        {
            return resolved;
        }

        string stripped = StripTrailingResolutionTag(title);
        if (string.Equals(stripped, title, StringComparison.Ordinal))
        {
            return resolved;
        }

        return new Dictionary<string, string>(resolved, StringComparer.OrdinalIgnoreCase)
        {
            [Tokens.Title] = stripped,
        };
    }

    // Renders with $ext suppressed and {} groups collapsed, then sanitizes the whole result as one
    // segment, so any '/' is stripped rather than treated as a separator.
    private static string RenderFilename(
        string template,
        IReadOnlyDictionary<string, string> resolved,
        RenamerOptions options,
        Action<string>? logUnbalanced)
    {
        string raw = RenderRaw(template, WithDeDupedTitle(resolved, template), suppressExt: true, logUnbalanced);
        raw = ApplyTransforms(raw, options);
        return Sanitizer.CleanSegment(raw, options);
    }

    // Splits the rendered text on '/', cleans each segment, drops empties and rejoins, so '/' stays
    // the path separator. An empty template renders empty, which means no folder move.
    private static string RenderFolder(
        string template,
        IReadOnlyDictionary<string, string> resolved,
        RenamerOptions options,
        Action<string>? logUnbalanced)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        string raw = RenderRaw(template, WithDeDupedTitle(resolved, template), suppressExt: false, logUnbalanced);
        raw = ApplyTransforms(raw, options);

        var cleaned = raw
            .Split('/')
            .Select(seg => Sanitizer.CleanSegment(seg, options))
            .Where(seg => seg.Length > 0);

        // Consecutive duplicate segments collapse after the per-segment clean and empty-drop, so the
        // comparison runs on the cleaned text.
        var collapsed = FieldRewriter.CollapseConsecutive(cleaned, options);
        return string.Join("/", collapsed);
    }

    // A {} group span is dropped whole, inner literals included, when every token inside it resolved
    // empty. Otherwise the group renders and only the empty tokens collapse, keeping their literals.
    // An unclosed GroupOpen renders as a normal group; the tokenizer literalizes a stray GroupClose.
    private static string RenderRaw(
        string template,
        IReadOnlyDictionary<string, string> resolved,
        bool suppressExt,
        Action<string>? logUnbalanced)
    {
        var segs = Tokenizer.Scan(template, logUnbalanced);
        var sb = new StringBuilder(template.Length);

        for (int i = 0; i < segs.Count; i++)
        {
            var seg = segs[i];
            switch (seg.Kind)
            {
                case SegKind.Literal:
                    sb.Append(seg.Text);
                    break;

                case SegKind.Token:
                    sb.Append(ResolveToken(seg.Text, resolved, suppressExt));
                    break;

                case SegKind.GroupOpen:
                    i = RenderGroup(segs, i, resolved, suppressExt, sb);
                    break;

                case SegKind.GroupClose:
                    // Unreachable at depth 0: a matching GroupOpen already consumed it.
                    break;
            }
        }

        return sb.ToString();
    }

    // Returns the index of the matching GroupClose, or the last consumed segment when unclosed, so
    // the caller's loop continues past the group. Appends nothing when every inner token is empty.
    private static int RenderGroup(
        List<Segment> segs,
        int openIdx,
        IReadOnlyDictionary<string, string> resolved,
        bool suppressExt,
        StringBuilder outer)
    {
        var inner = new StringBuilder();
        bool anyTokenNonEmpty = false;
        bool sawToken = false;
        int i = openIdx + 1;

        for (; i < segs.Count; i++)
        {
            var seg = segs[i];
            if (seg.Kind == SegKind.GroupClose)
            {
                break;
            }

            if (seg.Kind == SegKind.Literal)
            {
                inner.Append(seg.Text);
            }
            else if (seg.Kind == SegKind.Token)
            {
                sawToken = true;
                string v = ResolveToken(seg.Text, resolved, suppressExt);
                if (v.Length > 0)
                {
                    anyTokenNonEmpty = true;
                    inner.Append(v);
                }
            }
            else if (seg.Kind == SegKind.GroupOpen)
            {
                // Groups are flat, so a nested open is not expected; render it inline.
                i = RenderGroup(segs, i, resolved, suppressExt, inner);
            }
        }

        // The span drops with its inner literals only when the group had tokens and all were empty.
        bool drop = sawToken && !anyTokenNonEmpty;
        if (!drop)
        {
            outer.Append(inner);
        }

        return i;
    }

    // $ext resolves empty under suppressExt, which the filename render sets, so the extension is
    // not duplicated.
    private static string ResolveToken(string name, IReadOnlyDictionary<string, string> resolved, bool suppressExt)
    {
        if (suppressExt && string.Equals(name, Tokens.Ext, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return Resolve(resolved, name);
    }

    // Order: punctuation normalization, then ASCII transliteration, then the case transform.
    private static string ApplyTransforms(string s, RenamerOptions options)
    {
        // Punctuation normalization runs first so a folded straight double-quote still reaches the
        // illegal-char step in CleanSegment, which every render path calls after this.
        if (options.NormalizePunctuation)
        {
            s = Sanitizer.NormalizePunctuation(s);
        }

        if (options.AsciiTransliterate)
        {
            s = Sanitizer.Transliterate(s);
        }

        return Sanitizer.ApplyCase(s, options.Case);
    }

    // Normalizes a raw extension token, with or without a leading dot, to the leading-dot form.
    private static string NormalizeExt(string ext)
    {
        if (string.IsNullOrEmpty(ext))
        {
            return string.Empty;
        }

        return ext.StartsWith('.') ? ext : "." + ext;
    }
}
