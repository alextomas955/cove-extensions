using System.Text;
using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Engine;

/// <summary>
/// Canonical core token names so the metadata projector and the UI agree on
/// the exact strings the engine resolves. Lookups are case-insensitive; these are the
/// canonical spellings.
/// </summary>
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

/// <summary>
/// The pure <see cref="Render"/> orchestrator:
/// it composes the primitives (<see cref="Tokenizer"/>, <see cref="Sanitizer"/>,
/// <see cref="MultiValue"/>, <see cref="ResolutionLabel"/>) and <see cref="RenamerOptions"/>
/// to turn a token dictionary into a sanitized, length-safe <see cref="RenamerResult"/>.
///
/// Pipeline: (1) build the effective resolved token map (scalar tokens, multi-value
/// performers/tags via <see cref="MultiValue.Resolve(IReadOnlyList{string}, Options.MultiValueOptions)"/>,
/// derived <c>$resolution</c>);
/// (2) render the filename and folder templates independently — walk the <see cref="Segment"/>
/// list, collapse <c>{}</c> spans whose every inner token resolved empty; (3) apply
/// the case/transliteration transforms; (4) sanitize per segment (filename as one segment so
/// <c>/</c> is stripped; folder split on <c>/</c>, each piece cleaned, rejoined with <c>/</c>);
/// (5) resolve the extension (never duplicated); (6) <see cref="LengthReducer.Fit"/>.
///
/// 100% pure: no <c>Path</c>/<c>File</c>/DB calls. Path-traversal confinement (<c>..</c>,
/// absolute paths) is deliberately the executor's job, not the engine's — the engine never
/// sees the library root.
/// </summary>
public static class TemplateEngine
{
    public static RenamerResult Render(
        IReadOnlyDictionary<string, string> tokens,
        IReadOnlyDictionary<string, IReadOnlyList<string>> multiValues,
        RenamerOptions options,
        Action<string>? logUnbalanced = null,
        IReadOnlyList<RenamerPerformer>? performers = null)
        => RenderWithDropped(tokens, multiValues, options, logUnbalanced, performers).result;

    /// <summary>
    /// Identical to <see cref="Render"/>, but also returns the set of <see cref="RenamerOptions.DropOrder"/>
    /// fields the length reducer actually dropped to make the name fit (empty when nothing was dropped).
    /// <see cref="Render"/> delegates here and discards the dropped list, so there is ONE rendering path
    /// and the two methods can never diverge. The dropped list is the engine's own signal from
    /// <see cref="LengthReducer.FitWithDropped"/> — NOT a diff of output strings.
    /// </summary>
    public static (RenamerResult result, IReadOnlyList<string> dropped) RenderWithDropped(
        IReadOnlyDictionary<string, string> tokens,
        IReadOnlyDictionary<string, IReadOnlyList<string>> multiValues,
        RenamerOptions options,
        Action<string>? logUnbalanced = null,
        IReadOnlyList<RenamerPerformer>? performers = null)
    {
        // (1) Build the effective resolved token map (case-insensitive keys).
        var resolved = BuildResolvedMap(tokens, multiValues, options, performers);

        // (5, partial) Resolve the extension up front so the filename template can reference
        // $ext without it appearing twice: the $ext token resolves empty during the
        // filename render, and the extension is appended as RenamerResult.Ext.
        string ext = NormalizeExt(Resolve(resolved, Tokens.Ext));

        // (2)+(3)+(4) Render filename: $ext suppressed inside the name; '/' stripped.
        string filename = RenderFilename(options.FilenameTemplate, resolved, options, logUnbalanced);

        // (2)+(3)+(4) Render folder independently: keeps '/' as a path separator.
        string folder = RenderFolder(options.FolderTemplate, resolved, options, logUnbalanced);

        // (6) Length-fit against BOTH caps (re-renders without dropped fields).
        return LengthReducer.FitWithDropped(
            folder, filename, ext, options,
            // re-render delegate: produce (folder, name) with the cumulative set of dropped
            // fields forced empty.
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

    /// <summary>
    /// Builds the effective resolved scalar token map: copies the caller's scalar tokens
    /// (case-insensitive), overrides <c>$performers</c>/<c>$tags</c> with the joined
    /// multi-value resolution, and derives <c>$resolution</c> from the height token when present.
    /// </summary>
    private static Dictionary<string, string> BuildResolvedMap(
        IReadOnlyDictionary<string, string> tokens,
        IReadOnlyDictionary<string, IReadOnlyList<string>> multiValues,
        RenamerOptions options,
        IReadOnlyList<RenamerPerformer>? performerRecords = null)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in tokens)
        {
            map[kv.Key] = kv.Value;
        }

        // Apply pure value-level field rewrites (e.g. squeezing spaces out of studio names) to the
        // scalar map BEFORE the multi-value overrides and render. Materialize the keys first so we do
        // not mutate the dictionary while enumerating it.
        foreach (var key in map.Keys.ToList())
        {
            map[key] = FieldRewriter.RewriteScalar(key, map[key], options);
        }

        // De-double the resolution: when the filename template renders $resolution AND the title
        // already ends in a resolution tag (common for libraries whose titles were imported from
        // filenames, e.g. "… [1080p]"), strip that trailing tag so the template's own [$resolution]
        // is the single source — otherwise the name would carry "[1080p] [1080p]". Only stripped when
        // the template actually appends a resolution; a template without $resolution keeps whatever the
        // title carries. Not a user option: there is no sensible reason to want the doubled tag.
        if (map.TryGetValue(Tokens.Title, out var titleValue)
            && TemplateRendersResolution(options.FilenameTemplate))
        {
            map[Tokens.Title] = StripTrailingResolutionTag(titleValue);
        }

        if (TryGetMulti(multiValues, Tokens.Performers, out var performers))
        {
            // Drop performers already named in the RESOLVED title (after the scalar rewrites above)
            // BEFORE MultiValue.Resolve applies MaxCount, so a dropped name frees an overflow slot.
            // Compare against the rewritten $title from the map.
            string resolvedTitle = map.TryGetValue(Tokens.Title, out var t) ? t : string.Empty;

            if (performerRecords is not null)
            {
                // Filter the RECORDS directly with the same whole-word title predicate, then feed the
                // survivors (in order, including duplicate names) to the record resolver. Filtering the
                // records themselves — rather than reselecting them from the surviving NAME list via a
                // name-keyed set — preserves per-position multiplicity, so when two performers share a
                // name only the matching positions are dropped and a surviving duplicate is kept.
                var survivors = FieldRewriter.DropPerformersInTitle(performerRecords, resolvedTitle, options);
                map[Tokens.Performers] = MultiValue.Resolve(survivors, options.Performers);
            }
            else
            {
                performers = FieldRewriter.DropPerformersInTitle(performers, resolvedTitle, options);
                map[Tokens.Performers] = MultiValue.Resolve(performers, options.Performers);
            }
        }

        if (TryGetMulti(multiValues, Tokens.Tags, out var tags))
        {
            map[Tokens.Tags] = MultiValue.Resolve(tags, options.Tags);
        }

        // Derive $resolution from height only if the caller didn't already supply it.
        if (!map.ContainsKey(Tokens.Resolution)
            && map.TryGetValue(Tokens.Height, out var h)
            && int.TryParse(h, out var height))
        {
            map[Tokens.Resolution] = ResolutionLabel.FromHeight(height);
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

    /// <summary>Case-insensitive token lookup; unknown/absent → empty.</summary>
    private static string Resolve(IReadOnlyDictionary<string, string> resolved, string name)
        => resolved.TryGetValue(name, out var v) ? v ?? string.Empty : string.Empty;

    /// <summary>
    /// True iff <paramref name="template"/> references the <c>$resolution</c> token, so the render will
    /// append a resolution and a trailing one already in the title would be a duplicate. Matches the
    /// bare <c>$resolution</c> the engine supports (there is no <c>${…}</c> form), case-insensitively,
    /// and only when the char after the token is not another token-name char (so <c>$resolutionx</c>
    /// does not match). Pure string scan — no regex.
    /// </summary>
    private static bool TemplateRendersResolution(string template)
    {
        const string tok = "$" + Tokens.Resolution; // "$resolution"
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

    /// <summary>
    /// Removes a single trailing resolution tag from <paramref name="value"/>, then re-trims trailing
    /// whitespace. A tag is a bracketed resolution label: either a fixed
    /// <see cref="ResolutionLabel.KnownLabels"/> entry (e.g. <c>[1080p]</c>, <c>[4k]</c>) OR a bare
    /// numeric-height progressive-scan label (<c>[368p]</c>, <c>[240p]</c>) — the sub-480 form
    /// <see cref="ResolutionLabel.FromHeight"/> now emits. Only a tag at the very END is removed
    /// (a bounded suffix scan, no regex), so a resolution mentioned mid-title is left untouched.
    /// </summary>
    /// <remarks>
    /// The generic <c>[&lt;digits&gt;p]</c> arm is what stops a doubled tag: a title imported as
    /// "Nikki [368p]" plus a template that appends <c>{ [$resolution]}</c> (which now renders "368p")
    /// would otherwise yield "Nikki [368p] [368p]" — matching ONLY the five fixed labels missed the
    /// sub-480 tag and left it to double. Matching any bracketed numeric-p (or 4k) suffix de-dupes it.
    /// </remarks>
    private static string StripTrailingResolutionTag(string value)
    {
        string trimmed = value.TrimEnd();

        // Fixed labels first (covers "4k" and the ≥480 buckets).
        foreach (var label in ResolutionLabel.KnownLabels)
        {
            string tag = "[" + label + "]";
            if (trimmed.EndsWith(tag, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[..^tag.Length].TrimEnd();
            }
        }

        // Generic trailing "[<digits>p]" (the sub-480 form, e.g. "[368p]") the fixed list does not carry.
        if (trimmed.EndsWith(']') && TryStripTrailingNumericResTag(trimmed, out var stripped))
        {
            return stripped.TrimEnd();
        }

        return value;
    }

    /// <summary>
    /// Strips a trailing <c>[&lt;digits&gt;p]</c> tag (one or more ASCII digits, then a <c>p</c>, in
    /// brackets) from <paramref name="s"/>, which MUST already end in <c>']'</c>. Returns false when the
    /// suffix is not that exact shape (e.g. <c>[POV]</c>, <c>[caufkb2cd9]</c>, <c>[28]</c> — a
    /// bracketed number with NO <c>p</c> is a serial/index, not a resolution, and must NOT be stripped).
    /// Bounded backward scan, no regex.
    /// </summary>
    private static bool TryStripTrailingNumericResTag(string s, out string stripped)
    {
        stripped = s;
        int close = s.Length - 1;              // index of ']'
        if (close < 3)
        {
            return false;                      // need at least "[Np]"
        }

        int i = close - 1;
        if (s[i] is not ('p' or 'P'))
        {
            return false;                      // must end "...p]"
        }

        i--;
        int digitsEnd = i;
        while (i >= 0 && char.IsAsciiDigit(s[i]))
        {
            i--;
        }

        if (i == digitsEnd || i < 0 || s[i] != '[')
        {
            return false;                      // no digits, or no matching '[' immediately before them
        }

        stripped = s[..i];                     // everything before the '['
        return true;
    }

    /// <summary>
    /// Live-preview helper for the required-field gate: resolves a single token name against the SAME
    /// effective token map the renderer uses (scalar tokens + joined multi-value performers/tags +
    /// derived <c>$resolution</c>), so a "required field" gate check matches what the engine would
    /// actually render. Case-insensitive; unknown/absent → empty. Pure: no I/O.
    /// </summary>
    public static string ResolveField(
        IReadOnlyDictionary<string, string> tokens,
        IReadOnlyDictionary<string, IReadOnlyList<string>> multiValues,
        RenamerOptions options,
        string field)
    {
        var resolved = BuildResolvedMap(tokens, multiValues, options);
        return Resolve(resolved, field);
    }

    /// <summary>
    /// Live-preview helper: renders the filename through the SAME pipeline as
    /// <see cref="RenderFilename"/> and reports whether the sanitize step actually changed the name
    /// — i.e. illegal chars were stripped/replaced or spaces replaced/collapsed/trimmed under the
    /// active options. Reuses the engine's own render+transform+clean steps (single source of truth);
    /// it does NOT diff against a TS re-implementation. Pure: no I/O.
    /// </summary>
    public static bool WouldSanitizeFilename(
        IReadOnlyDictionary<string, string> tokens,
        IReadOnlyDictionary<string, IReadOnlyList<string>> multiValues,
        RenamerOptions options)
    {
        var resolved = BuildResolvedMap(tokens, multiValues, options);
        string raw = RenderRaw(options.FilenameTemplate, resolved, options, suppressExt: true, null);
        raw = ApplyTransforms(raw, options);
        return Sanitizer.CleanSegment(raw, options) != raw;
    }

    /// <summary>
    /// Renders the filename template: emits the raw text (tokens resolved, <c>$ext</c> suppressed,
    /// <c>{}</c> groups collapsed), applies case/transliteration, then sanitizes the whole result
    /// as ONE segment so any <c>/</c> is stripped.
    /// </summary>
    private static string RenderFilename(
        string template,
        IReadOnlyDictionary<string, string> resolved,
        RenamerOptions options,
        Action<string>? logUnbalanced)
    {
        string raw = RenderRaw(template, resolved, options, suppressExt: true, logUnbalanced);
        raw = ApplyTransforms(raw, options);
        return Sanitizer.CleanSegment(raw, options);
    }

    /// <summary>
    /// Renders the folder template: emits the raw text, applies transforms, then splits on
    /// <c>/</c>, cleans each segment, drops empties, and rejoins with <c>/</c> — keeping <c>/</c>
    /// only as the path separator. An empty template → empty (no folder move).
    /// </summary>
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

        string raw = RenderRaw(template, resolved, options, suppressExt: false, logUnbalanced);
        raw = ApplyTransforms(raw, options);

        var cleaned = raw
            .Split('/')
            .Select(seg => Sanitizer.CleanSegment(seg, options))
            .Where(seg => seg.Length > 0);

        // Collapse consecutive duplicate folder segments AFTER per-segment clean + empty-drop and
        // BEFORE the '/'-join (no-op when the flag is off). The filename render never reaches here.
        var collapsed = FieldRewriter.CollapseConsecutive(cleaned, options);
        return string.Join("/", collapsed);
    }

    /// <summary>
    /// Walks the <see cref="Tokenizer.Scan"/> segment list, resolving tokens and collapsing
    /// <c>{}</c> groups: a group span is dropped entirely (including its inner literals) iff
    /// EVERY token inside it resolved empty; otherwise the group renders with empty tokens
    /// collapsed (their inner literals stay). Unbalanced braces are already
    /// degraded by the tokenizer (GroupOpen with no matching GroupClose at EOF is treated as a
    /// normal group here; a stray GroupClose cannot occur because the scanner literalizes it).
    /// </summary>
    private static string RenderRaw(
        string template,
        IReadOnlyDictionary<string, string> resolved,
        RenamerOptions options,
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
                    // Unreachable at depth 0 (scanner only emits GroupClose when balanced and
                    // a matching GroupOpen consumed it via RenderGroup). Ignore defensively.
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders a single <c>{...}</c> group starting at <paramref name="openIdx"/> (a GroupOpen).
    /// Returns the index of the matching GroupClose (or the last consumed segment if unclosed),
    /// so the caller's loop continues after it. Appends nothing if every inner token is empty.
    /// </summary>
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
                // Groups are flat; a nested open is not expected, but render it inline
                // defensively so we never crash.
                i = RenderGroup(segs, i, resolved, suppressExt, inner);
            }
        }

        // Drop the WHOLE span (incl. inner literals) iff the group had tokens and all were empty.
        bool drop = sawToken && !anyTokenNonEmpty;
        if (!drop)
        {
            outer.Append(inner);
        }

        return i; // i points at the GroupClose (or segs.Count if unclosed) — loop's i++ moves past it.
    }

    /// <summary>
    /// Resolves a single token name. <c>$ext</c> resolves empty when <paramref name="suppressExt"/>
    /// is set (the filename render) so the extension is never duplicated.
    /// </summary>
    private static string ResolveToken(string name, IReadOnlyDictionary<string, string> resolved, bool suppressExt)
    {
        if (suppressExt && string.Equals(name, Tokens.Ext, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return Resolve(resolved, name);
    }

    /// <summary>Applies the configured punctuation normalization, then ASCII transliteration, then case transform.</summary>
    private static string ApplyTransforms(string s, RenamerOptions options)
    {
        // Punctuation normalization runs FIRST so a folded straight double-quote is still subsequently
        // stripped by the illegal-char step in CleanSegment (every render path calls this before it).
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

    /// <summary>Normalizes a raw extension token (e.g. <c>mkv</c> or <c>.mkv</c>) to a leading-dot form, or empty.</summary>
    private static string NormalizeExt(string ext)
    {
        if (string.IsNullOrEmpty(ext))
        {
            return string.Empty;
        }

        return ext.StartsWith('.') ? ext : "." + ext;
    }
}
