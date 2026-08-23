using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace Renamer.Options;

/// <summary>Optional case transform applied to a rendered name.</summary>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum CaseTransform { None, Lower, Title }

/// <summary>What to do when a multi-value field exceeds its max count.</summary>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum OverflowPolicy { DropAll, KeepFirst }

/// <summary>
/// Sort order for a multi-value field's items.
/// <see cref="IdAsc"/> and <see cref="FavoriteFirst"/> apply only to performers (they need the
/// per-performer id/favorite data); tags fall back to name ordering for them. There is
/// deliberately no rating order: performer rating is per-user data and the detached renamer job
/// runs without a signed-in user, so there is no defined rating to order by.
/// </summary>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
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

    /// <summary>
    /// If non-empty, only items whose STABLE id is listed survive. Keyed on the id — never the name —
    /// exactly like <see cref="RenamerOptions.StudioDestinations"/> and
    /// <see cref="RenamerOptions.ExcludeTagIds"/>, so renaming a performer or a tag in Cove cannot
    /// orphan the rule and two spelling variants of one item can never be treated as two. The joined
    /// output still renders NAMES; the id only decides who survives.
    /// </summary>
    public List<int> WhitelistIds { get; init; } = [];

    /// <summary>
    /// If non-empty, items whose STABLE id is listed are removed. Keyed exactly like
    /// <see cref="WhitelistIds"/>, and applied after it.
    /// </summary>
    public List<int> BlacklistIds { get; init; } = [];

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

    // Record value equality compares List<string> members by reference, so a JSON round-trip (which
    // allocates fresh lists) would never be Equal to the original. Both Equals and GetHashCode run off
    // the SAME EqualityComponents list, whose collection members are wrapped to compare by VALUE.
    public bool Equals(MultiValueOptions? other)
        => other is not null && StructuralEquality.Members(EqualityComponents(), other.EqualityComponents());

    public override int GetHashCode() => StructuralEquality.Hash(EqualityComponents());

    private IEnumerable<object?> EqualityComponents()
    {
        yield return Separator;
        yield return MaxCount;
        yield return OnOverflow;
        yield return Sort;
        yield return StructuralEquality.Sequence(WhitelistIds);
        yield return StructuralEquality.Sequence(BlacklistIds);
        yield return StructuralEquality.Sequence(IgnoreGenders);
        yield return StructuralEquality.Sequence(GenderOrder);
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
/// Where a matched item lands: a <see cref="Root"/> CHOSEN from Cove's own library paths, plus a
/// relative <see cref="Template"/> rendered underneath it. Every destination in this extension has
/// this one shape — the per-studio, per-tag and source-path rules, the unorganized route, and the
/// default (<see cref="RenamerOptions.FolderRoot"/> + <see cref="RenamerOptions.FolderTemplate"/>)
/// alike — so there is nothing to combine and no precedence to teach.
/// </summary>
/// <remarks>
/// No path is ever typed. Cove already owns the library paths, and a typed absolute path is a copy of
/// a value that lives upstream: change the library path in Cove and every typed copy silently points
/// at nothing. <see cref="Root"/> is a REFERENCE into that list, re-read on every plan, so a root the
/// user removed from Cove stops the rule loudly (<c>SkipRootMissing</c>) instead of writing somewhere
/// nobody chose.
/// <para>
/// The two empty values are the two useful defaults, not two spellings of "unset". An empty
/// <see cref="Root"/> means "the file's own library path" — the containing root, so a rule can tidy
/// each drive in place. An empty <see cref="Template"/> means the root itself. BOTH empty is the one
/// state that moves nothing: a destination naming neither a root nor a folder asks for nothing, which
/// is what an unconfigured <i>Where files go</i> has always meant.
/// </para>
/// <para>
/// Hand-written structural <c>Equals</c>/<c>GetHashCode</c> for the same reason
/// <see cref="PathDestinationRule"/> has them: a JSON round-trip allocates a fresh instance and the
/// settings panel's dirty check compares by value.
/// </para>
/// </remarks>
public sealed record Destination
{
    /// <summary>The chosen Cove library path, or <c>""</c> = the library path containing the file.</summary>
    public string Root { get; init; } = "";

    /// <summary>The relative folder template rendered under <see cref="Root"/>; <c>""</c> = the root itself.</summary>
    public string Template { get; init; } = "";

    public bool Equals(Destination? other)
        => other is not null && Root == other.Root && Template == other.Template;

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(Root);
        hc.Add(Template);
        return hc.ToHashCode();
    }
}

/// <summary>
/// One source-path destination rule: when the entity's source path matches
/// <see cref="Pattern"/>, the item routes to <see cref="Dest"/>.
/// <see cref="IsRegex"/> selects how <see cref="Pattern"/> is interpreted:
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

    /// <summary>Where a matched item lands — a library root plus a relative template.</summary>
    public Destination Dest { get; init; } = new();

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
    public string FilenameTemplate { get; init; } = "{$date - }$title{ [$resolution]}";

    /// <summary>
    /// The DEFAULT destination's relative folder template — the panel's <i>Where files go</i>, applied
    /// to an item no destination rule matched. Rendered under <see cref="FolderRoot"/>.
    /// </summary>
    /// <remarks>
    /// Empty (the shipped default) together with an empty <see cref="FolderRoot"/> is the
    /// moves-nothing state described at <see cref="Destination"/>: unmatched items are renamed where
    /// they stand. This field is ALSO the effective template the engine renders — the planner
    /// substitutes a matched rule's own template into it, so every downstream consumer (both folder
    /// renders and the length reducer's re-render) reads the one value rather than each deciding for
    /// itself which template applies.
    /// </remarks>
    public string FolderTemplate { get; init; } = "";

    /// <summary>
    /// The DEFAULT destination's root: a Cove library path, or <c>""</c> = the library path containing
    /// the file. Pairs with <see cref="FolderTemplate"/> to make one <see cref="Destination"/>.
    /// </summary>
    /// <remarks>
    /// Two flat fields rather than a nested <see cref="Destination"/> because
    /// <see cref="FolderTemplate"/> is also the engine's render input (see its remarks); the SHAPE is
    /// the same root-plus-relative-template every rule has.
    /// </remarks>
    public string FolderRoot { get; init; } = "";
    public string DateFormat { get; init; } = "yyyy-MM-dd";
    public string DurationFormat { get; init; } = @"hh\-mm\-ss";
    public MultiValueOptions Performers { get; init; } = new() { Separator = " " };
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
    public string RemoveCharacters { get; init; } = ",#";  // default strips comma + hash; "" = remove nothing

    /// <summary>
    /// Fallback: when an item has no title, derive <c>$title</c> from the item's first file's basename
    /// (extension stripped) instead of omitting the token — and RECORD that title on the item, which is
    /// the only way the derivation stops re-reading its own output (see
    /// <c>MetadataProjector.DerivedTitle</c>).
    /// </summary>
    /// <remarks>
    /// Default <c>false</c>, and the default is the setting rather than a detail of it: this is the one
    /// option that makes a rename write metadata instead of only moving a file, and writing into a
    /// person's library is a thing to be asked for. The earlier <c>true</c> default was chosen to keep a
    /// title-less item from being skipped by the <c>title</c>-required gate — but being skipped is the
    /// safe answer for an item this extension has no metadata for, and the skip says so where a
    /// silently-invented title does not. A previously-saved value is preserved on load, so this governs
    /// a first run only.
    /// </remarks>
    public bool FilenameAsTitle { get; init; }

    /// <summary>
    /// Opt-in: after a move relocates a file out of its source directory, delete that source directory
    /// when the move leaves it completely empty. Default <c>false</c>. The delete is only-if-empty and
    /// non-recursive, never touches a non-empty or root directory, and a failed delete never fails the
    /// move (the move already succeeded). Has no effect on a same-folder renamer (the source dir still
    /// holds the file).
    /// </summary>
    public bool RemoveEmptyFolder { get; init; }

    public bool AsciiTransliterate { get; init; }

    /// <summary>
    /// When <c>true</c>, a small punctuation-only set of typographic characters is folded to ASCII in
    /// the rendered name (curly quotes → straight quotes, en/em dashes → hyphen, ellipsis → three dots).
    /// <remarks>
    /// Punctuation-only — accented letters and non-Latin scripts are untouched (that is
    /// <c>AsciiTransliterate</c>). Default <c>true</c> because scrapers store smart quotes/dashes in
    /// metadata while the on-disk basenames are plain ASCII, so folding punctuation back keeps those
    /// straight-quote files as no-ops rather than rewriting them to carry curly punctuation. A
    /// previously-saved value is preserved on load (the default applies only to a first run).
    /// </remarks>
    /// </summary>
    public bool NormalizePunctuation { get; init; } = true;

    public int FilenameMax { get; init; } = 255;
    public int FullPathMax { get; init; } = 259;
    public List<string> DropOrder { get; init; } =
        ["videoCodec", "audioCodec", "frameRate", "resolution", "tags", "studioCode", "studio", "performers", "date"];

    /// <summary>
    /// An optional NARROWING of where a rename may write: when non-empty, a resolved target folder is
    /// accepted only if it also lies inside one of these absolute directories. Empty (the shipped
    /// default) applies no narrowing.
    /// </summary>
    /// <remarks>
    /// It cannot WIDEN, and that is the whole of what it now is. A destination is a Cove library path
    /// plus a relative, sanitized template, so every target is inside the library by construction and
    /// there is no escape left for a permission list to refuse; drawing this list narrower than a
    /// library path is the only thing it can still express — "rename inside this subtree only". It
    /// never decides where a relative template is PLACED: that is the destination's own root.
    /// </remarks>
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
    /// (not renamed), so un-curated items don't get junk names. Default <c>false</c> = renamer all.
    /// <para>
    /// An <see cref="UnorganizedDestination"/> takes PRECEDENCE over this gate — see that member for
    /// the whole statement, including which of its states may fall through to here.
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
    /// event handler re-renames the item (respecting gating). Default <c>false</c> = the hook is a no-op.
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
    /// Studio routing map: stable studio <c>Id</c> → <see cref="Destination"/>. The studio cascade keys
    /// on this id (never the name) so a name typo/sanitization variant can never split one studio
    /// across two destination trees. Default empty = no studio routing.
    /// </summary>
    public Dictionary<int, Destination> StudioDestinations { get; init; } = [];

    /// <summary>
    /// Tag routing map: stable tag <c>Id</c> → <see cref="Destination"/>. The tag cascade keys on this
    /// id (never the name), exactly like <see cref="StudioDestinations"/>, so renaming a tag in Cove
    /// cannot orphan or mis-target its rule and two spelling variants of one tag can never split
    /// across two destination trees. Default empty = no tag routing.
    /// </summary>
    public Dictionary<int, Destination> TagDestinations { get; init; } = [];

    /// <summary>
    /// Source-path routing rules, in user order. Each <see cref="PathDestinationRule"/> is an exact OR
    /// regex source-path match → destination; the resolver tries exact rules before regex rules within
    /// the source-path category. The regex variant is a user-interpreted pattern — pre-parsed/validated
    /// once at build time, ReDoS-bounded by a match timeout (see <see cref="PathDestinationRule"/>).
    /// Default empty = no source-path routing.
    /// </summary>
    public List<PathDestinationRule> PathDestinations { get; init; } = [];

    /// <summary>
    /// Tag excludes: STABLE tag ids (never the name), keyed exactly like <see cref="TagDestinations"/>
    /// and mirroring <see cref="ExcludeStudioIds"/>. An item carrying any of these tags is EXCLUDED
    /// from renamer/move BEFORE any routing category is considered (excludes are evaluated first),
    /// surfaced as a visible <c>SkipExcluded</c> in the preview. Default empty = no tag excludes
    /// (legacy behavior, no regression).
    /// </summary>
    public List<int> ExcludeTagIds { get; init; } = [];

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
    /// Unorganized destination: the route for an item whose <c>Organized</c> flag is false. Resolved
    /// at the unorganized precedence slot (before the tag/studio/path cascade), so an unorganized item
    /// routes here rather than being skipped. Default <c>null</c> = no unorganized route.
    /// <para>
    /// When set, this OVERRIDES <see cref="OnlyOrganized"/> for unorganized items — the item routes
    /// here instead of being gated out, so the unorganized route is never silently nullified by the
    /// only-organized gate.
    /// </para>
    /// <para>
    /// Nullable rather than a <see cref="Destination"/> whose emptiness means "off", because the two
    /// states are genuinely different questions: <c>null</c> is "there is no unorganized route", while
    /// a present destination naming neither root nor folder is a route that deliberately moves nothing.
    /// Only the first may fall through to the only-organized gate.
    /// </para>
    /// </summary>
    public Destination? UnorganizedDestination { get; init; }

    /// <summary>
    /// Free-space safety margin: the number of bytes left FREE on each destination volume
    /// beyond the projected file bytes before a cross-drive batch is allowed to proceed. The
    /// free-space guard adds this to a volume's summed need before comparing against its available
    /// space, so a batch never fills a disk to the brim. Default <c>1 GiB</c> (<c>1L &lt;&lt; 30</c>).
    /// Same-volume renames are excluded from the sum, so this margin only gates cross-drive moves.
    /// </summary>
    public long FreeSpaceHeadroomBytes { get; init; } = 1L << 30;

    /// <summary>
    /// Cross-drive concurrency bound: the maximum number of simultaneous cross-drive transfers per
    /// (source,destination) disk pair. Same-volume renames are unthrottled (an atomic
    /// <c>File.Move</c> consumes no extra space). Default <c>2</c> — conservative, to avoid thrashing
    /// two spinning disks with too many concurrent copies.
    /// </summary>
    public int CrossVolumeConcurrency { get; init; } = 2;

    /// <summary>
    /// Same-volume parallelism bound: the maximum number of simultaneous same-drive renames within one
    /// batch. A same-drive renamer is an instant metadata <c>File.Move</c> that consumes no extra space,
    /// so this is not a space guard — it is a pressure bound. An unbounded fan-out (the old <c>-1</c>)
    /// let a large selection issue thousands of concurrent <c>File.Move</c> + per-worker DB scope +
    /// event-bus operations at once; this caps the in-flight count while staying high enough that a
    /// normal batch sees full parallelism. The default is a fixed <c>8</c> (not
    /// <c>Environment.ProcessorCount</c>, so the serialized default stays byte-identical across
    /// machines). A value &lt;= 0 is treated as unbounded for backward compatibility.
    /// </summary>
    public int SameVolumeConcurrency { get; init; } = 8;

    // Record value equality would compare the List/Dictionary members by REFERENCE, so a JSON round-trip
    // (fresh instances) would never be Equal. Both Equals and GetHashCode run off the SAME
    // EqualityComponents list — one source of truth rather than twin member lists, so a new member
    // added to one can never be forgotten in the other (the twin-list footgun). Each
    // collection member is wrapped to compare by VALUE: order-SENSITIVE for lists, order-INDEPENDENT for
    // the destination maps (a Dictionary has no guaranteed order and a round-trip may reorder keys), with
    // the map's original key comparer preserved.
    public bool Equals(RenamerOptions? other)
        => other is not null && StructuralEquality.Members(EqualityComponents(), other.EqualityComponents());

    public override int GetHashCode() => StructuralEquality.Hash(EqualityComponents());

    private IEnumerable<object?> EqualityComponents()
    {
        yield return FilenameTemplate;
        yield return FolderTemplate;
        yield return FolderRoot;
        yield return DateFormat;
        yield return DurationFormat;
        yield return Performers;
        yield return Tags;
        yield return IllegalReplacement;
        yield return SpaceReplacement;
        yield return Case;
        yield return RemoveCharacters;
        yield return FilenameAsTitle;
        yield return RemoveEmptyFolder;
        yield return AsciiTransliterate;
        yield return NormalizePunctuation;
        yield return FilenameMax;
        yield return FullPathMax;
        yield return OnlyOrganized;
        yield return DuplicateSuffixFormat;
        yield return AutoRenamerOnUpdate;
        yield return SqueezeStudioNames;
        yield return StripLeadingArticles;
        yield return StructuralEquality.Sequence(FieldReplacers);
        yield return StructuralEquality.Sequence(Articles);
        yield return PreventTitlePerformer;
        yield return PreventConsecutiveSegments;
        yield return StructuralEquality.Sequence(DropOrder);
        yield return StructuralEquality.Sequence(RequiredFields);
        yield return StructuralEquality.Sequence(AllowedRoots);
        yield return StructuralEquality.Sequence(AssociatedExtensions);
        yield return StructuralEquality.Map(StudioDestinations, EqualityComparer<int>.Default);
        yield return StructuralEquality.Map(TagDestinations, EqualityComparer<int>.Default);
        yield return StructuralEquality.Sequence(PathDestinations);
        yield return StructuralEquality.Sequence(ExcludeTagIds);
        yield return StructuralEquality.Sequence(ExcludeStudioIds);
        yield return StructuralEquality.Sequence(ExcludePaths);
        yield return UnorganizedDestination;
        yield return FreeSpaceHeadroomBytes;
        yield return CrossVolumeConcurrency;
        yield return SameVolumeConcurrency;
    }

    /// <summary>
    /// Shared serializer settings used by both save and load so the round-trip is symmetric:
    /// case-insensitive property names (forward-compat for hand-edited blobs) and
    /// enums as stable strings. <c>OptionsStore</c> reuses this exact instance.
    /// </summary>
    /// <remarks>
    /// These options are also the one home for every body this extension parses ITSELF, and the reason
    /// it parses them at all: the host's default minimal-API <see cref="JsonSerializerOptions"/> carry
    /// no <see cref="JsonStringEnumConverter"/>, so a body holding a string enum value
    /// (<c>"case":"Lower"</c>) fails typed binding with a 400 BEFORE the handler runs, and extension
    /// code cannot reach host startup (<c>ConfigureHttpJsonOptions</c>) to fix that globally. An
    /// endpoint that must accept such a body therefore binds the raw request and deserializes here.
    /// The blob's PascalCase spelling is never the obstacle — property binding is case-insensitive on
    /// both paths — so retyping such a parameter is not the simplification it looks like.
    /// </remarks>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>
/// Drives a record's <c>Equals</c>/<c>GetHashCode</c> from ONE component list instead of a
/// hand-maintained twin member list — a new member added to one but forgotten in the other (the
/// twin-list footgun) is impossible when both consume the same <c>EqualityComponents()</c>. Collection
/// members are wrapped so they compare by VALUE, not by reference, which is what a JSON round-trip
/// (fresh instances) needs to stay Equal to the original.
/// </summary>
internal static class StructuralEquality
{
    /// <summary>Value-compares two component sequences position by position (an order-sensitive AND of the members).</summary>
    public static bool Members(IEnumerable<object?> a, IEnumerable<object?> b) => a.SequenceEqual(b);

    /// <summary>Hashes a component sequence; consistent with <see cref="Members"/> because both consume the same components.</summary>
    public static int Hash(IEnumerable<object?> components)
    {
        var hc = new HashCode();
        foreach (var component in components)
        {
            hc.Add(component);
        }

        return hc.ToHashCode();
    }

    /// <summary>Wraps an ordered collection so equality is an order-SENSITIVE element compare (mirrors <c>SequenceEqual</c>).</summary>
    public static object Sequence<T>(IReadOnlyCollection<T> items) => new SeqKey<T>(items);

    /// <summary>Wraps a map so equality is an ORDER-INDEPENDENT compare under <paramref name="keyComparer"/> (a round-trip may reorder keys).</summary>
    public static object Map<TKey, TValue>(Dictionary<TKey, TValue> map, IEqualityComparer<TKey> keyComparer)
        where TKey : notnull => new MapKey<TKey, TValue>(map, keyComparer);

    private readonly struct SeqKey<T> : IEquatable<SeqKey<T>>
    {
        private readonly IReadOnlyCollection<T> _items;
        public SeqKey(IReadOnlyCollection<T> items) => _items = items;

        public bool Equals(SeqKey<T> other) => _items.SequenceEqual(other._items);
        public override bool Equals(object? obj) => obj is SeqKey<T> other && Equals(other);

        public override int GetHashCode()
        {
            var hc = new HashCode();
            foreach (var item in _items)
            {
                hc.Add(item);
            }

            return hc.ToHashCode();
        }
    }

    private readonly struct MapKey<TKey, TValue> : IEquatable<MapKey<TKey, TValue>>
        where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> _map;
        private readonly IEqualityComparer<TKey> _keyComparer;

        public MapKey(Dictionary<TKey, TValue> map, IEqualityComparer<TKey> keyComparer)
        {
            _map = map;
            _keyComparer = keyComparer;
        }

        public bool Equals(MapKey<TKey, TValue> other)
        {
            if (_map.Count != other._map.Count)
            {
                return false;
            }

            // Build the lookup by assignment (last write wins on a key collision under the comparer),
            // matching the prior hand-rolled comparison rather than the throwing dictionary(source,
            // comparer) constructor.
            var lookup = new Dictionary<TKey, TValue>(other._map.Count, _keyComparer);
            foreach (var kv in other._map)
            {
                lookup[kv.Key] = kv.Value;
            }

            foreach (var kv in _map)
            {
                if (!lookup.TryGetValue(kv.Key, out var value)
                    || !EqualityComparer<TValue>.Default.Equals(value, kv.Value))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is MapKey<TKey, TValue> other && Equals(other);

        public override int GetHashCode()
        {
            // Order-independent XOR accumulator, keyed through the comparer so it stays consistent with
            // the order-independent, comparer-aware Equals above.
            int acc = 0;
            foreach (var kv in _map)
            {
                acc ^= HashCode.Combine(_keyComparer.GetHashCode(kv.Key), kv.Value);
            }

            return acc;
        }
    }
}
