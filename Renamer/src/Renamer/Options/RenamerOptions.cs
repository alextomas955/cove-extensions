using System.Text.Json;
using System.Text.Json.Serialization;

namespace Renamer.Options;

/// <summary>Optional case transform applied to a rendered name.</summary>
public enum CaseTransform { None, Lower, Title }

/// <summary>What to do when a multi-value field exceeds its max count.</summary>
public enum OverflowPolicy { DropAll, KeepFirst }

/// <summary>
/// Sort order for a multi-value field's items.
/// <see cref="IdAsc"/> and <see cref="FavoriteFirst"/> apply only to performers (they need the
/// per-performer id/favorite data); tags fall back to name ordering for them. There is
/// deliberately no rating order: performer rating is per-user data and the detached renamer job
/// runs without a signed-in user, so there is no defined rating to order by.
/// </summary>
public enum SortOrder
{
    /// <summary>Order by name, case-insensitively (the default).</summary>
    NameAsc,

    /// <summary>Preserve the input order.</summary>
    None,

    /// <summary>Order performers by ascending id.</summary>
    IdAsc,

    /// <summary>Order performers with favorites first, then by name.</summary>
    FavoriteFirst,
}

/// <summary>
/// Per-field controls for a multi-value token (performers, tags).
/// A C# record with <c>init</c> properties + default initializers so a missing
/// JSON property naturally falls back to its default and the instance is immutable.
/// </summary>
public sealed record MultiValueOptions
{
    /// <summary>String inserted between joined items.</summary>
    public string Separator { get; init; } = ", ";

    /// <summary>Maximum items to emit; <c>0</c> = unlimited.</summary>
    public int MaxCount { get; init; }

    /// <summary>Behavior when <see cref="MaxCount"/> is exceeded.</summary>
    public OverflowPolicy OnOverflow { get; init; } = OverflowPolicy.DropAll;

    /// <summary>Sort applied before joining.</summary>
    public SortOrder Sort { get; init; } = SortOrder.NameAsc;

    /// <summary>If non-empty, only these values are kept (case-insensitive).</summary>
    public List<string> Whitelist { get; init; } = [];

    /// <summary>If non-empty, these values are removed (case-insensitive).</summary>
    public List<string> Blacklist { get; init; } = [];

    /// <summary>
    /// Performer-only: genders to drop entirely (case-insensitive). Applied BEFORE the max-count
    /// limit, so dropping a gender frees an overflow slot for another performer. A performer with no
    /// gender set is always kept. Empty = no gender filtering.
    /// </summary>
    public List<string> IgnoreGenders { get; init; } = [];

    /// <summary>
    /// Performer-only: a preferred gender ordering, most-preferred first (case-insensitive). When
    /// non-empty it reorders performers so the listed genders come first in this order; any gender
    /// not listed (and the no-gender case) sorts last. Applied as a stable order AFTER the chosen
    /// <see cref="Sort"/> and BEFORE the max-count limit, so it controls which performers survive the
    /// limit. Empty = no gender ordering.
    /// </summary>
    public List<string> GenderOrder { get; init; } = [];

    // Record value equality compares List<string> members by reference, so a JSON
    // round-trip (which allocates fresh lists) would never be Equal to the original.
    // Compare the list members structurally so save/load round-trips are value-equal.
    public bool Equals(MultiValueOptions? other)
        => other is not null
        && Separator == other.Separator
        && MaxCount == other.MaxCount
        && OnOverflow == other.OnOverflow
        && Sort == other.Sort
        && Whitelist.SequenceEqual(other.Whitelist)
        && Blacklist.SequenceEqual(other.Blacklist)
        && IgnoreGenders.SequenceEqual(other.IgnoreGenders)
        && GenderOrder.SequenceEqual(other.GenderOrder);

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(Separator);
        hc.Add(MaxCount);
        hc.Add(OnOverflow);
        hc.Add(Sort);
        foreach (var v in Whitelist)
        {
            hc.Add(v);
        }

        foreach (var v in Blacklist)
        {
            hc.Add(v);
        }

        foreach (var v in IgnoreGenders)
        {
            hc.Add(v);
        }

        foreach (var v in GenderOrder)
        {
            hc.Add(v);
        }

        return hc.ToHashCode();
    }
}

/// <summary>
/// One per-field literal find/replace rule: replaces every literal occurrence of
/// <see cref="Find"/> with <see cref="Replace"/> in the value of the <see cref="TargetToken"/>
/// token (matched case-insensitively against the canonical <c>Tokens</c> names). This is a
/// literal substring replace — NOT a regex — so an arbitrary user <see cref="Find"/> can
/// never trigger catastrophic-backtracking. A record with <c>init</c> props + default
/// initializers + a hand-written structural <c>Equals</c>/<c>GetHashCode</c> so a JSON round-trip
/// (which allocates a fresh instance) compares value-equal.
/// </summary>
public sealed record FieldReplaceRule
{
    /// <summary>Canonical token name (case-insensitive) whose value this rule rewrites.</summary>
    public string TargetToken { get; init; } = "";

    /// <summary>Literal substring to find (NOT a regex). An empty find is a no-op (skipped).</summary>
    public string Find { get; init; } = "";

    /// <summary>Literal replacement substring.</summary>
    public string Replace { get; init; } = "";

    public bool Equals(FieldReplaceRule? other)
        => other is not null
        && TargetToken == other.TargetToken
        && Find == other.Find
        && Replace == other.Replace;

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(TargetToken);
        hc.Add(Find);
        hc.Add(Replace);
        return hc.ToHashCode();
    }
}

/// <summary>
/// One source-path destination rule: when the entity's source path matches
/// <see cref="Pattern"/>, the item routes to <see cref="Dest"/> (an absolute destination-root
/// template). <see cref="IsRegex"/> selects how <see cref="Pattern"/> is interpreted:
/// <c>false</c> = an EXACT source-path match (the common, safe case); <c>true</c> = the pattern is
/// a .NET regex matched against the source path.
///
/// The regex variant is a user-authored pattern interpreted as a regex. To bound
/// catastrophic-backtracking (ReDoS) — the same caution <see cref="FieldReplaceRule"/> avoided
/// entirely by using LITERAL replace — the regex is PRE-PARSED and VALIDATED exactly once when the
/// per-batch <c>RouteLookups</c> is built: an invalid regex rule is rejected at parse/build time, not
/// at match time, and the resolver only ever calls <c>IsMatch</c> on an already-compiled pattern
/// (with a match timeout applied at build time). A record with <c>init</c> props + a hand-written
/// structural <c>Equals</c>/<c>GetHashCode</c> so a JSON round-trip (which allocates a fresh instance)
/// compares value-equal.
/// </summary>
public sealed record PathDestinationRule
{
    /// <summary>Source-path pattern: an exact path when <see cref="IsRegex"/> is false, else a .NET regex (pre-parsed/validated at build time).</summary>
    public string Pattern { get; init; } = "";

    /// <summary>Absolute destination-root template the matched item routes to.</summary>
    public string Dest { get; init; } = "";

    /// <summary>When <c>true</c>, <see cref="Pattern"/> is interpreted as a regex; otherwise an exact source-path match.</summary>
    public bool IsRegex { get; init; }

    public bool Equals(PathDestinationRule? other)
        => other is not null
        && Pattern == other.Pattern
        && Dest == other.Dest
        && IsRegex == other.IsRegex;

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(Pattern);
        hc.Add(Dest);
        hc.Add(IsRegex);
        return hc.ToHashCode();
    }
}

/// <summary>
/// One source-path exclude rule: when the entity's source path matches <see cref="Pattern"/>,
/// the item is EXCLUDED from renamer/move (a visible skip-with-reason for every file), regardless of
/// any routing rule it would otherwise match. <see cref="IsRegex"/> selects interpretation:
/// <c>false</c> = an EXACT source-path match (the common, safe case); <c>true</c> = the pattern is a
/// .NET regex matched against the source path.
///
/// Like <see cref="PathDestinationRule"/>, the regex variant is a user-authored pattern: it is
/// PRE-PARSED and VALIDATED exactly once when the per-batch exclude lookups are built (an invalid
/// regex is skipped-with-a-log at build time, never at match time, and a match-time
/// catastrophic-backtracking timeout is treated as no-match — never thrown). A record with
/// <c>init</c> props + a hand-written structural <c>Equals</c>/<c>GetHashCode</c> so a JSON
/// round-trip (which allocates a fresh instance) compares value-equal. Carries NO destination —
/// an excluded item is never moved.
/// </summary>
public sealed record ExcludeRule
{
    /// <summary>Source-path pattern: an exact path when <see cref="IsRegex"/> is false, else a .NET regex (pre-parsed/validated at build time).</summary>
    public string Pattern { get; init; } = "";

    /// <summary>When <c>true</c>, <see cref="Pattern"/> is interpreted as a regex; otherwise an exact source-path match.</summary>
    public bool IsRegex { get; init; }

    public bool Equals(ExcludeRule? other)
        => other is not null
        && Pattern == other.Pattern
        && IsRegex == other.IsRegex;

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(Pattern);
        hc.Add(IsRegex);
        return hc.ToHashCode();
    }
}

/// <summary>
/// All renamer settings (template + sanitization + length + multi-value), with sensible defaults.
/// Serialized as a single forward-compatible System.Text.Json blob (unknown props ignored on load,
/// missing props default).
/// </summary>
public sealed record RenamerOptions
{
    public string FilenameTemplate { get; init; } = "{$date - }$title{ [$height]}";
    public string FolderTemplate { get; init; } = "";        // empty = no folder move
    public string DateFormat { get; init; } = "yyyy-MM-dd";
    public string DurationFormat { get; init; } = @"hh\-mm\-ss";
    public MultiValueOptions Performers { get; init; } = new() { Separator = ", " };
    public MultiValueOptions Tags { get; init; } = new() { Separator = " " };
    public string IllegalReplacement { get; init; } = "";    // "" = strip
    public string SpaceReplacement { get; init; } = "";      // "" = keep spaces
    public CaseTransform Case { get; init; } = CaseTransform.None;

    /// <summary>
    /// A literal set of characters dropped from the rendered name, distinct from the OS-illegal
    /// strip: a char listed here is removed outright before the illegal/space handling runs, so a
    /// char that is both listed and OS-illegal is removed rather than first becoming the
    /// <see cref="IllegalReplacement"/>. Not a regex (literal membership, ReDoS-free). Empty = no-op.
    /// </summary>
    public string RemoveCharacters { get; init; } = "";    // "" = remove nothing

    /// <summary>
    /// Fallback: when an item has no title, derive <c>$title</c> from the file's basename
    /// (extension stripped) instead of omitting the token. Default <c>true</c> = a fresh install
    /// gives a title-less item a name from its basename rather than skipping it under the
    /// <c>title</c>-required gate. A previously-saved value is preserved on load (the default applies
    /// only to a first run); set it <c>false</c> to keep the strict omit-not-blank behavior.
    /// </summary>
    public bool FilenameAsTitle { get; init; } = true;

    /// <summary>
    /// Opt-in: after a move relocates a file out of its source directory, delete that source directory
    /// when the move leaves it completely empty. Default <c>false</c>. The delete is only-if-empty and
    /// non-recursive, never touches a non-empty or root directory, and a failed delete never fails the
    /// move (the move already succeeded). Has no effect on a same-folder renamer (the source dir still
    /// holds the file).
    /// </summary>
    public bool RemoveEmptyFolder { get; init; }

    public bool AsciiTransliterate { get; init; }
    public int FilenameMax { get; init; } = 255;
    public int FullPathMax { get; init; } = 259;
    public List<string> DropOrder { get; init; } =
        ["videoCodec", "audioCodec", "frameRate", "resolution", "tags", "studioCode", "studio", "performers", "date"];

    /// <summary>
    /// The set of absolute directories a renamer is permitted to write into. A target folder
    /// (including one produced by a rooted folder template) is accepted only when it normalizes
    /// to a path inside one of these roots; a target under no listed root is rejected. The empty
    /// default keeps the original behavior where a renamer can only stay within the file's own
    /// source folder and a rooted folder template is refused outright — so adding a root is an
    /// explicit, opt-in widening of where files may move.
    /// </summary>
    public List<string> AllowedRoots { get; init; } = [];

    /// <summary>
    /// File extensions whose same-basename neighbor files move and renamer alongside the primary,
    /// supplementing the DB-tracked caption sidecars Cove already follows. A neighbor is taken only
    /// when it shares the primary's exact stem AND its extension is listed here, so this never widens
    /// into a broad directory sweep. Extensions are normalized at use (a single leading <c>.</c> is
    /// stripped and the compare is ordinal-ignore-case), so <c>srt</c>, <c>.srt</c> and <c>SRT</c> all
    /// match the same file. Stored raw (no normalization on the option itself) so a UI round-trip stays
    /// value-equal. Empty default = no extension sidecars discovered (byte-identical to caption-only).
    /// </summary>
    public List<string> AssociatedExtensions { get; init; } = [];

    /// <summary>
    /// Gating: when <c>true</c>, an item whose <c>Organized</c> flag is false is skipped
    /// (not renamerd), so un-curated items don't get junk names. Default <c>false</c> = renamer all.
    /// <para>
    /// A configured <see cref="UnorganizedDestination"/> takes PRECEDENCE over this gate. When an
    /// unorganized destination is set, an unorganized item is NOT skipped — it routes to that
    /// destination, since routing unorganized items is the whole point of that destination. This gate
    /// skips unorganized items only when no <see cref="UnorganizedDestination"/> is configured.
    /// </para>
    /// </summary>
    public bool OnlyOrganized { get; init; }

    /// <summary>
    /// Gating: token names (case-insensitive) that must resolve non-empty or the item is
    /// skipped. Default <c>["title"]</c> — a Title-less item is skipped. An empty list = no
    /// required-field gate.
    /// </summary>
    public List<string> RequiredFields { get; init; } = ["title"];

    /// <summary>
    /// Collision suffix: a format string whose <c>{n}</c> placeholder is replaced by the
    /// collision counter (1, 2, …) and inserted before the extension when a target name is taken.
    /// Default <c>" ({n})"</c> → <c>"name.mp4" → "name (1).mp4"</c>.
    /// </summary>
    public string DuplicateSuffixFormat { get; init; } = " ({n})";

    /// <summary>
    /// Auto-renamer hook opt-in: when <c>true</c>, the <c>video.updated</c>/<c>image.updated</c>
    /// event handler re-renamers the item (respecting gating). Default <c>false</c> = the hook is a no-op.
    /// </summary>
    public bool AutoRenamerOnUpdate { get; init; }

    /// <summary>
    /// When <c>true</c>, all space characters are removed from the <c>$studio</c> token's value
    /// (e.g. <c>Reality Kings</c> → <c>RealityKings</c>) so one logical studio renders to one stable
    /// folder name and never splits across destination trees. Targets the <c>$studio</c> token
    /// specifically. Default <c>false</c> = output unchanged.
    /// </summary>
    public bool SqueezeStudioNames { get; init; }

    /// <summary>
    /// A list of per-token literal find/replace rules applied to a scalar token's value (e.g. strip
    /// <c>'</c> from <c>$studio</c> only) BEFORE the squeeze and article steps, independent of the
    /// global illegal/space replacement. Literal substring replace, NOT a regex. Default empty =
    /// output unchanged.
    /// </summary>
    public List<FieldReplaceRule> FieldReplacers { get; init; } = [];

    /// <summary>
    /// When <c>true</c>, a single LEADING article (see <see cref="Articles"/>) followed by whitespace
    /// is stripped from the <c>$title</c> token's value (<c>The Matrix</c> → <c>Matrix</c>), at most
    /// once, with the remaining leading whitespace re-trimmed. Default <c>false</c> = output unchanged.
    /// </summary>
    public bool StripLeadingArticles { get; init; }

    /// <summary>
    /// The leading articles eligible for <see cref="StripLeadingArticles"/>. Matching is
    /// case-insensitive and only a single leading article followed by whitespace is stripped, so
    /// <c>Theatre</c> and a mid-title <c>The</c> are untouched. Default <c>["The", "A", "An"]</c>.
    /// </summary>
    public List<string> Articles { get; init; } = ["The", "A", "An"];

    /// <summary>
    /// When <c>true</c>, a performer whose (trimmed) name appears as a whole-word, case-insensitive
    /// occurrence in the resolved <c>$title</c> is dropped from the performers list BEFORE the
    /// <c>MultiValue.Resolve</c> join (so a dropped name also frees an overflow slot). Default
    /// <c>false</c> = output unchanged.
    /// </summary>
    public bool PreventTitlePerformer { get; init; }

    /// <summary>
    /// When <c>true</c>, consecutive duplicate segments in the rendered FOLDER path collapse to one
    /// (<c>/Foo/Foo/Bar</c> → <c>/Foo/Bar</c>, case-insensitive, first kept), applied in
    /// <c>RenderFolder</c> after per-segment sanitize and before the <c>/</c>-join; the filename render
    /// is untouched. Default <c>true</c> = a fresh install collapses a duplicated folder segment
    /// (cosmetic, folder-path only). A previously-saved value is preserved on load (the default applies
    /// only to a first run).
    /// </summary>
    public bool PreventConsecutiveSegments { get; init; } = true;

    /// <summary>
    /// Studio routing map: stable studio <c>Id</c> → absolute destination-root template. The studio
    /// cascade keys on this id (never the name) so a name typo/sanitization variant can never split
    /// one studio across two destination trees. Default empty = no studio routing (legacy
    /// source-confine behavior).
    /// </summary>
    public Dictionary<int, string> StudioDestinations { get; init; } = [];

    /// <summary>
    /// Tag routing map: tag NAME → absolute destination-root template, keyed and compared
    /// case-insensitively (a rule for <c>"Anime"</c> matches an entity tagged <c>"anime"</c>).
    /// Tag routing keys on the name (matching the existing flattened tag-name list). Default empty =
    /// no tag routing (legacy source-confine behavior).
    /// </summary>
    public Dictionary<string, string> TagDestinations { get; init; } = [];

    /// <summary>
    /// Source-path routing rules, in user order. Each <see cref="PathDestinationRule"/> is an exact OR
    /// regex source-path match → destination; the resolver tries exact rules before regex rules within
    /// the source-path category. The regex variant is a user-interpreted pattern — pre-parsed/validated
    /// once at build time, ReDoS-bounded by a match timeout (see <see cref="PathDestinationRule"/>).
    /// Default empty = no source-path routing.
    /// </summary>
    public List<PathDestinationRule> PathDestinations { get; init; } = [];

    /// <summary>
    /// Tag excludes: tag NAMES (matched case-insensitively, mirroring tag routing). An item carrying
    /// any of these tags is EXCLUDED from renamer/move BEFORE any routing category is considered
    /// (excludes are evaluated first), surfaced as a visible <c>SkipExcluded</c> in the preview.
    /// Default empty = no tag excludes (legacy behavior, no regression).
    /// </summary>
    public List<string> ExcludeTags { get; init; } = [];

    /// <summary>
    /// Studio excludes: STABLE studio ids (never the name). An item is excluded when its own
    /// <c>StudioId</c> OR any of its <c>ParentStudios</c> ancestor ids is in this set ("studio or its
    /// parent"), keyed on the stable id exactly like <see cref="StudioDestinations"/> so a name
    /// typo/variant can never mis-target an exclude. Excludes run FIRST. Default empty.
    /// </summary>
    public List<int> ExcludeStudioIds { get; init; } = [];

    /// <summary>
    /// Source-path excludes, in user order: each <see cref="ExcludeRule"/> is an exact OR regex
    /// source-path match (mirroring <see cref="PathDestinations"/>); a matching item is excluded from
    /// renamer/move. The regex variant is pre-parsed/validated once at build time and ReDoS-bounded by a
    /// match timeout (see <see cref="ExcludeRule"/>). Excludes run FIRST. Default empty.
    /// </summary>
    public List<ExcludeRule> ExcludePaths { get; init; } = [];

    /// <summary>
    /// Default destination: the absolute root for an item that matched NO rule. Honored ONLY when
    /// <see cref="EnableDefaultRelocate"/> is <c>true</c> (a hard gate). Default <c>""</c> = no
    /// default route.
    /// </summary>
    public string DefaultDestination { get; init; } = "";

    /// <summary>
    /// Unorganized destination: the route for an item whose <c>Organized</c> flag is false. Resolved
    /// at the unorganized precedence slot (before the tag/studio/path cascade), so an unorganized item
    /// routes here rather than being skipped. Default <c>""</c> = no unorganized route.
    /// <para>
    /// When set, this OVERRIDES <see cref="OnlyOrganized"/> for unorganized items — the item routes
    /// here instead of being gated out, so the unorganized route is never silently nullified by the
    /// only-organized gate.
    /// </para>
    /// </summary>
    public string UnorganizedDestination { get; init; } = "";

    /// <summary>
    /// Hard-gate flag: default-relocate of an UNMATCHED item to <see cref="DefaultDestination"/> is
    /// enabled ONLY when this is <c>true</c>. It ships <c>false</c> and STAYS disabled until
    /// volume-aware undo is delivered — because a runaway default-relocate has whole-library blast
    /// radius and undo is the only recovery. The resolver enforces this as a code path (the off branch
    /// returns SourceConfine), not merely a config default.
    /// </summary>
    public bool EnableDefaultRelocate { get; init; }

    /// <summary>
    /// Free-space safety margin: the number of bytes left FREE on each destination volume
    /// beyond the projected file bytes before a cross-drive batch is allowed to proceed. The
    /// free-space guard adds this to a volume's summed need before comparing against its available
    /// space, so a batch never fills a disk to the brim. Default <c>1 GiB</c> (<c>1L &lt;&lt; 30</c>).
    /// Same-volume renamers are excluded from the sum, so this margin only gates cross-drive moves.
    /// </summary>
    public long FreeSpaceHeadroomBytes { get; init; } = 1L << 30;

    /// <summary>
    /// Cross-drive concurrency bound: the maximum number of simultaneous cross-drive transfers per
    /// (source,destination) disk pair. Same-volume renamers are unthrottled (an atomic
    /// <c>File.Move</c> consumes no extra space). Default <c>2</c> — conservative, to avoid thrashing
    /// two spinning disks with too many concurrent copies.
    /// </summary>
    public int CrossVolumeConcurrency { get; init; } = 2;

    /// <summary>
    /// Same-volume parallelism bound: the maximum number of simultaneous same-drive renamers within one
    /// batch. A same-drive renamer is an instant metadata <c>File.Move</c> that consumes no extra space,
    /// so this is not a space guard — it is a pressure bound. An unbounded fan-out (the old <c>-1</c>)
    /// let a large selection issue thousands of concurrent <c>File.Move</c> + per-worker DB scope +
    /// event-bus operations at once; this caps the in-flight count while staying high enough that a
    /// normal batch sees full parallelism. The default is a fixed <c>8</c> (not
    /// <c>Environment.ProcessorCount</c>, so the serialized default stays byte-identical across
    /// machines). A value &lt;= 0 is treated as unbounded for backward compatibility.
    /// </summary>
    public int SameVolumeConcurrency { get; init; } = 8;

    // Same reasoning as MultiValueOptions: DropOrder/RequiredFields (List<string>) and the two
    // MultiValueOptions members must compare structurally for a round-trip to be Equal.
    public bool Equals(RenamerOptions? other)
        => other is not null
        && FilenameTemplate == other.FilenameTemplate
        && FolderTemplate == other.FolderTemplate
        && DateFormat == other.DateFormat
        && DurationFormat == other.DurationFormat
        && Performers == other.Performers
        && Tags == other.Tags
        && IllegalReplacement == other.IllegalReplacement
        && SpaceReplacement == other.SpaceReplacement
        && Case == other.Case
        && RemoveCharacters == other.RemoveCharacters
        && FilenameAsTitle == other.FilenameAsTitle
        && RemoveEmptyFolder == other.RemoveEmptyFolder
        && AsciiTransliterate == other.AsciiTransliterate
        && FilenameMax == other.FilenameMax
        && FullPathMax == other.FullPathMax
        && OnlyOrganized == other.OnlyOrganized
        && DuplicateSuffixFormat == other.DuplicateSuffixFormat
        && AutoRenamerOnUpdate == other.AutoRenamerOnUpdate
        && SqueezeStudioNames == other.SqueezeStudioNames
        && StripLeadingArticles == other.StripLeadingArticles
        && FieldReplacers.SequenceEqual(other.FieldReplacers)
        && Articles.SequenceEqual(other.Articles)
        && PreventTitlePerformer == other.PreventTitlePerformer
        && PreventConsecutiveSegments == other.PreventConsecutiveSegments
        && DropOrder.SequenceEqual(other.DropOrder)
        && RequiredFields.SequenceEqual(other.RequiredFields)
        && AllowedRoots.SequenceEqual(other.AllowedRoots)
        && AssociatedExtensions.SequenceEqual(other.AssociatedExtensions)
        && DictMapEqual(StudioDestinations, other.StudioDestinations, EqualityComparer<int>.Default)
        && DictMapEqual(TagDestinations, other.TagDestinations, StringComparer.OrdinalIgnoreCase)
        && PathDestinations.SequenceEqual(other.PathDestinations)
        && ExcludeTags.SequenceEqual(other.ExcludeTags)
        && ExcludeStudioIds.SequenceEqual(other.ExcludeStudioIds)
        && ExcludePaths.SequenceEqual(other.ExcludePaths)
        && DefaultDestination == other.DefaultDestination
        && UnorganizedDestination == other.UnorganizedDestination
        && EnableDefaultRelocate == other.EnableDefaultRelocate
        && FreeSpaceHeadroomBytes == other.FreeSpaceHeadroomBytes
        && CrossVolumeConcurrency == other.CrossVolumeConcurrency
        && SameVolumeConcurrency == other.SameVolumeConcurrency;

    // Order-independent set-of-pairs comparison for a Dictionary<TKey,string>, parameterized by the
    // key comparer so the int-keyed StudioDestinations and the OrdinalIgnoreCase TagDestinations share
    // one helper. A Dictionary has no guaranteed order and a JSON round-trip may reorder keys.
    private static bool DictMapEqual<TKey>(
        Dictionary<TKey, string> a,
        Dictionary<TKey, string> b,
        IEqualityComparer<TKey> keyComparer)
        where TKey : notnull
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        var lookup = new Dictionary<TKey, string>(b.Count, keyComparer);
        foreach (var kv in b)
        {
            lookup[kv.Key] = kv.Value;
        }

        foreach (var kv in a)
        {
            if (!lookup.TryGetValue(kv.Key, out var bv) || bv != kv.Value)
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(FilenameTemplate);
        hc.Add(FolderTemplate);
        hc.Add(DateFormat);
        hc.Add(DurationFormat);
        hc.Add(Performers);
        hc.Add(Tags);
        hc.Add(IllegalReplacement);
        hc.Add(SpaceReplacement);
        hc.Add(Case);
        hc.Add(RemoveCharacters);
        hc.Add(FilenameAsTitle);
        hc.Add(RemoveEmptyFolder);
        hc.Add(AsciiTransliterate);
        hc.Add(FilenameMax);
        hc.Add(FullPathMax);
        hc.Add(OnlyOrganized);
        hc.Add(DuplicateSuffixFormat);
        hc.Add(AutoRenamerOnUpdate);
        hc.Add(SqueezeStudioNames);
        hc.Add(StripLeadingArticles);
        foreach (var rule in FieldReplacers)
        {
            hc.Add(rule);
        }

        foreach (var v in Articles)
        {
            hc.Add(v);
        }

        hc.Add(PreventTitlePerformer);
        hc.Add(PreventConsecutiveSegments);

        foreach (var v in DropOrder)
        {
            hc.Add(v);
        }

        foreach (var v in RequiredFields)
        {
            hc.Add(v);
        }

        foreach (var v in AllowedRoots)
        {
            hc.Add(v);
        }

        foreach (var v in AssociatedExtensions)
        {
            hc.Add(v);
        }

        // Order-independent (XOR-accumulator) for the dictionaries, matching DictMapEqual.
        int studioAcc = 0;
        foreach (var kv in StudioDestinations)
        {
            studioAcc ^= HashCode.Combine(kv.Key, kv.Value);
        }

        hc.Add(studioAcc);

        int tagAcc = 0;
        foreach (var kv in TagDestinations)
        {
            tagAcc ^= HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(kv.Key),
                kv.Value);
        }

        hc.Add(tagAcc);

        foreach (var rule in PathDestinations)
        {
            hc.Add(rule);
        }

        foreach (var v in ExcludeTags)
        {
            hc.Add(v);
        }

        foreach (var id in ExcludeStudioIds)
        {
            hc.Add(id);
        }

        foreach (var rule in ExcludePaths)
        {
            hc.Add(rule);
        }

        hc.Add(DefaultDestination);
        hc.Add(UnorganizedDestination);
        hc.Add(EnableDefaultRelocate);
        hc.Add(FreeSpaceHeadroomBytes);
        hc.Add(CrossVolumeConcurrency);
        hc.Add(SameVolumeConcurrency);

        return hc.ToHashCode();
    }

    /// <summary>
    /// Shared serializer settings used by both save and load so the round-trip is symmetric:
    /// case-insensitive property names (forward-compat for hand-edited blobs) and
    /// enums as stable strings. <c>OptionsStore</c> reuses this exact instance.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
