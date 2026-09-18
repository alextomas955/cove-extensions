using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace Renamer.Options;

[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum CaseTransform { None, Lower, Title }

/// <summary>What to do when a multi-value field exceeds its max count.</summary>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum OverflowPolicy { DropAll, KeepFirst }

/// <summary>Sort order for a multi-value field's items.</summary>
/// <remarks>
/// <see cref="IdAsc"/> and <see cref="FavoriteFirst"/> apply only to performers, which are the only
/// items carrying id and favorite data; tags fall back to name ordering for them. There is no rating
/// order: performer rating is per-user data and the detached renamer job runs with no signed-in user.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum SortOrder
{
    /// <summary>Order by name, case-insensitively.</summary>
    NameAsc,

    /// <summary>Preserve the input order.</summary>
    None,

    IdAsc,

    /// <summary>Order performers with favorites first, then by name.</summary>
    FavoriteFirst,
}

/// <summary>Per-field controls for a multi-value token (performers, tags).</summary>
public sealed record MultiValueOptions
{
    public string Separator { get; init; } = ", ";

    /// <summary>Maximum items to emit; <c>0</c> = unlimited.</summary>
    public int MaxCount { get; init; }

    public OverflowPolicy OnOverflow { get; init; } = OverflowPolicy.DropAll;

    /// <summary>Sort applied before joining.</summary>
    public SortOrder Sort { get; init; } = SortOrder.NameAsc;

    /// <summary>If non-empty, only the entities with these stable ids are kept.</summary>
    /// <remarks>
    /// Keyed on the id, so renaming a tag or performer in Cove cannot silently stop a rule from
    /// matching. The token still renders the current name, so a rename shows up in the output while the
    /// rule keeps applying to the same entity.
    /// </remarks>
    public List<int> WhitelistIds { get; init; } = [];

    /// <summary>If non-empty, the entities with these stable ids are removed.</summary>
    public List<int> BlacklistIds { get; init; } = [];

    /// <summary>Performer-only: genders to drop entirely, case-insensitive.</summary>
    /// <remarks>
    /// Applied before the max-count limit, so dropping a gender frees an overflow slot for another
    /// performer. A performer with no gender set is always kept. Empty does no gender filtering.
    /// </remarks>
    public List<string> IgnoreGenders { get; init; } = [];

    /// <summary>Performer-only: a preferred gender ordering, most-preferred first, case-insensitive.</summary>
    /// <remarks>
    /// Applied as a stable order after the chosen <see cref="Sort"/> and before the max-count limit, so
    /// it controls which performers survive the limit. Any gender not listed, and the no-gender case,
    /// sorts last. Empty does no gender ordering.
    /// </remarks>
    public List<string> GenderOrder { get; init; } = [];

    // Record value equality compares the list members by reference, so a JSON round-trip would never be
    // equal. Equals and GetHashCode both run off EqualityComponents, whose collection members are
    // wrapped to compare by value.
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
/// One per-field literal find/replace rule: every occurrence of <see cref="Find"/> in the
/// <see cref="TargetToken"/> token's value becomes <see cref="Replace"/>.
/// </summary>
/// <remarks>
/// <see cref="TargetToken"/> is matched case-insensitively against the canonical <c>Tokens</c> names.
/// The replace is a literal substring replace, not a regex, so a user-authored <see cref="Find"/>
/// cannot trigger catastrophic backtracking.
/// </remarks>
public sealed record FieldReplaceRule
{
    public string TargetToken { get; init; } = "";

    /// <summary>Literal substring to find; an empty find is skipped.</summary>
    public string Find { get; init; } = "";

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
/// Where a matched item lands: a <see cref="Root"/> chosen from Cove's own library paths, plus a
/// relative <see cref="Template"/> rendered underneath it.
/// </summary>
/// <remarks>
/// <see cref="Root"/> is a reference into Cove's library paths, re-read on every plan, so a root the
/// user has since removed from Cove stops the rule with
/// <see cref="Planner.RenamerStatus.SkipRootMissing"/>. The two empty values are defaults, not two
/// spellings of "unset": an empty <see cref="Root"/> means the library path containing the file, and
/// an empty <see cref="Template"/> means the root itself. Both empty moves nothing.
/// </remarks>
public sealed record Destination
{
    public string Root { get; init; } = "";

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
/// The per-entity-kind settings: whether this extension renames the kind at all, and the default
/// destination its items take when no routing rule matches them.
/// </summary>
/// <remarks>
/// An absent entry means enabled with no destination of its own. <see cref="Destination"/> is nullable
/// because a present destination naming neither root nor folder is a real instruction, rename in place
/// under the library path the file is already in, and only <c>null</c> falls through to the global
/// default. A kind destination is the default for the kind, not an override of a matched rule: a tag,
/// studio, source-path or unorganized rule still wins.
/// </remarks>
public sealed record KindOptions
{
    public bool Enabled { get; init; } = true;

    public Destination? Destination { get; init; }

    public bool Equals(KindOptions? other)
        => other is not null && Enabled == other.Enabled && Destination == other.Destination;

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(Enabled);
        hc.Add(Destination);
        return hc.ToHashCode();
    }
}

/// <summary>
/// One source-path destination rule: an item whose source path matches <see cref="Pattern"/> routes to
/// <see cref="Dest"/>.
/// </summary>
/// <remarks>
/// <see cref="IsRegex"/> selects how <see cref="Pattern"/> is read: an exact source-path match, or a
/// .NET regex matched against the source path. A regex is user-authored, so it is parsed, validated and
/// given a match timeout exactly once when the per-batch <c>RouteLookups</c> is built; an invalid rule
/// is rejected there, and the resolver only ever calls <c>IsMatch</c> on an already-compiled pattern.
/// That bounds catastrophic backtracking.
/// </remarks>
public sealed record PathDestinationRule
{
    public string Pattern { get; init; } = "";

    public Destination Dest { get; init; } = new();

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
/// One source-path exclude rule: an item whose source path matches <see cref="Pattern"/> is excluded
/// from rename and move, whatever routing rule it would otherwise match.
/// </summary>
/// <remarks>
/// <see cref="IsRegex"/> selects how <see cref="Pattern"/> is read: an exact source-path match, or a
/// .NET regex parsed and validated exactly once when the per-batch exclude lookups are built. An
/// invalid regex is skipped with a log at build time, and a match-time backtracking timeout counts as
/// no match and is never thrown. The rule carries no destination, since an excluded item is never
/// moved.
/// </remarks>
public sealed record ExcludeRule
{
    public string Pattern { get; init; } = "";

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

/// <summary>All renamer settings, with defaults.</summary>
/// <remarks>
/// Serialized as a single forward-compatible System.Text.Json blob: unknown properties are ignored on
/// load and missing properties take their default.
/// </remarks>
public sealed record RenamerOptions
{
    public string FilenameTemplate { get; init; } = "{$date - }$title{ [$resolution]}";

    /// <summary>
    /// The default destination's relative folder template, rendered under <see cref="FolderRoot"/>;
    /// empty means the root itself.
    /// </summary>
    /// <remarks>
    /// Also the effective template the engine renders: the planner substitutes a matched rule's own
    /// template into it, so every downstream consumer reads this one value.
    /// </remarks>
    public string FolderTemplate { get; init; } = "";

    /// <summary>
    /// The default destination's root: a Cove library path, or <c>""</c> for the library path containing
    /// the file.
    /// </summary>
    public string FolderRoot { get; init; } = "";
    public string DateFormat { get; init; } = "yyyy-MM-dd";
    public string DurationFormat { get; init; } = @"hh\-mm\-ss";
    public MultiValueOptions Performers { get; init; } = new() { Separator = " " };
    public MultiValueOptions Tags { get; init; } = new() { Separator = " " };
    public string IllegalReplacement { get; init; } = "";    // "" = strip
    public string SpaceReplacement { get; init; } = "";      // "" = keep spaces
    public CaseTransform Case { get; init; } = CaseTransform.None;

    /// <summary>A literal set of characters dropped from the rendered name.</summary>
    /// <remarks>
    /// Removed outright before the illegal and space handling runs, so a character that is both listed
    /// here and OS-illegal is removed and never becomes <see cref="IllegalReplacement"/>. Literal
    /// membership, not a regex. Empty is a no-op.
    /// </remarks>
    public string RemoveCharacters { get; init; } = ",#";

    /// <summary>
    /// When <c>true</c>, an item with no title derives <c>$title</c> from its first file's basename with
    /// the extension stripped.
    /// </summary>
    /// <remarks>
    /// The derived title is recorded on the item, which is what stops the derivation re-reading its own
    /// output (see <c>MetadataProjector.DerivedTitle</c>). Set <c>false</c> to keep the strict
    /// omit-not-blank behavior, under which a title-less item is skipped by the <c>title</c>-required
    /// gate and nothing is written.
    /// </remarks>
    public bool FilenameAsTitle { get; init; } = true;

    /// <summary>
    /// When <c>true</c>, a move that leaves the file's source directory completely empty deletes that
    /// directory.
    /// </summary>
    /// <remarks>
    /// The delete is only-if-empty and non-recursive, never touches a non-empty or root directory, and a
    /// failed delete never fails the move. A same-folder rename leaves the file in its source directory,
    /// so nothing is deleted.
    /// </remarks>
    public bool RemoveEmptyFolder { get; init; }

    public bool AsciiTransliterate { get; init; }

    /// <summary>
    /// When <c>true</c>, typographic punctuation in the rendered name is folded to ASCII: curly quotes
    /// to straight quotes, en and em dashes to a hyphen, an ellipsis to three dots.
    /// </summary>
    /// <remarks>
    /// Punctuation only. Accented letters and non-Latin scripts are untouched, which is
    /// <see cref="AsciiTransliterate"/>. Scrapers store smart quotes and dashes in metadata while the
    /// on-disk basenames are plain ASCII, so folding punctuation back keeps those straight-quote files
    /// as no-ops.
    /// </remarks>
    public bool NormalizePunctuation { get; init; } = true;

    public int FilenameMax { get; init; } = 255;
    public int FullPathMax { get; init; } = 259;
    public List<string> DropOrder { get; init; } =
        ["videoCodec", "audioCodec", "frameRate", "resolution", "tags", "studioCode", "studio", "performers", "date"];

    /// <summary>These options with any length cap that cannot be a budget replaced by its default.</summary>
    /// <remarks>
    /// <see cref="Engine.LengthReducer"/> clamps a negative budget to zero and hard-truncates the
    /// basename to nothing, and an empty name looks like a result. A cap that is merely tight is left
    /// alone, since a small positive budget is a configuration a user chose. <c>OptionsStore</c> applies
    /// this to a blob that bound.
    /// </remarks>
    public RenamerOptions WithUsableLengthCaps()
    {
        if (FilenameMax > 0 && FullPathMax > 0)
        {
            return this;
        }

        var defaults = new RenamerOptions();
        return this with
        {
            FilenameMax = FilenameMax > 0 ? FilenameMax : defaults.FilenameMax,
            FullPathMax = FullPathMax > 0 ? FullPathMax : defaults.FullPathMax,
        };
    }

    /// <summary>
    /// File extensions whose same-basename neighbor files move and rename alongside the primary,
    /// supplementing the caption sidecars Cove already tracks in the database.
    /// </summary>
    /// <remarks>
    /// A neighbor is taken only when it shares the primary's exact stem and its extension is listed
    /// here, so this never widens into a directory sweep. Extensions are normalized at use, one leading
    /// <c>.</c> stripped and the compare ordinal-ignore-case, and stored raw so a UI round-trip stays
    /// value-equal. Empty discovers no extension sidecars.
    /// </remarks>
    public List<string> AssociatedExtensions { get; init; } = [];

    /// <summary>When <c>true</c>, an item whose <c>Organized</c> flag is false is skipped.</summary>
    /// <remarks>
    /// A configured <see cref="UnorganizedDestination"/> takes precedence over this gate: an unorganized
    /// item then routes to that destination, and the gate applies only when no unorganized destination
    /// is set.
    /// </remarks>
    public bool OnlyOrganized { get; init; }

    /// <summary>
    /// Token names, case-insensitive, that must resolve non-empty or the item is skipped; an empty list
    /// removes the gate.
    /// </summary>
    public List<string> RequiredFields { get; init; } = ["title"];

    /// <summary>
    /// Suffix inserted before the extension when a target name is taken; <c>{n}</c> is replaced by the
    /// collision counter.
    /// </summary>
    public string DuplicateSuffixFormat { get; init; } = " ({n})";

    /// <summary>
    /// When <c>true</c>, the <c>video.updated</c> and <c>image.updated</c> handler re-renames the item,
    /// respecting the gates.
    /// </summary>
    public bool AutoRenamerOnUpdate { get; init; }

    /// <summary>
    /// When <c>true</c>, all space characters are removed from the <c>$studio</c> token's value.
    /// </summary>
    /// <remarks>
    /// One logical studio then renders to one stable folder name and never splits across destination
    /// trees.
    /// </remarks>
    public bool SqueezeStudioNames { get; init; }

    /// <summary>Per-token literal find/replace rules applied to a scalar token's value.</summary>
    /// <remarks>
    /// Applied before the squeeze and article steps, and independent of the global illegal and space
    /// replacement. Literal substring replace, not a regex.
    /// </remarks>
    public List<FieldReplaceRule> FieldReplacers { get; init; } = [];

    /// <summary>
    /// When <c>true</c>, a single leading article from <see cref="Articles"/> followed by whitespace is
    /// stripped from the <c>$title</c> token's value.
    /// </summary>
    /// <remarks>At most one article is stripped, and the remaining leading whitespace is re-trimmed.</remarks>
    public bool StripLeadingArticles { get; init; }

    /// <summary>The leading articles eligible for <see cref="StripLeadingArticles"/>.</summary>
    /// <remarks>
    /// Matching is case-insensitive and needs the trailing whitespace, so <c>Theatre</c> and a mid-title
    /// <c>The</c> are untouched.
    /// </remarks>
    public List<string> Articles { get; init; } = ["The", "A", "An"];

    /// <summary>
    /// When <c>true</c>, a performer whose trimmed name occurs as a whole word in the resolved
    /// <c>$title</c> is dropped from the performers list.
    /// </summary>
    /// <remarks>
    /// The occurrence is matched case-insensitively, and the drop happens before the
    /// <c>MultiValue.Resolve</c> join, so a dropped name also frees an overflow slot.
    /// </remarks>
    public bool PreventTitlePerformer { get; init; }

    /// <summary>
    /// When <c>true</c>, consecutive duplicate segments in the rendered folder path collapse to one.
    /// </summary>
    /// <remarks>
    /// Case-insensitive with the first kept, applied in <c>RenderFolder</c> after the per-segment
    /// sanitize and before the <c>/</c>-join. The filename render is untouched.
    /// </remarks>
    public bool PreventConsecutiveSegments { get; init; } = true;

    /// <summary>Studio routing map: stable studio <c>Id</c> to <see cref="Destination"/>.</summary>
    /// <remarks>
    /// The studio cascade keys on this id, never the name, so a name typo or sanitization variant can
    /// never split one studio across two destination trees.
    /// </remarks>
    public Dictionary<int, Destination> StudioDestinations { get; init; } = [];

    /// <summary>Tag routing map: stable tag <c>Id</c> to <see cref="Destination"/>.</summary>
    /// <remarks>
    /// The tag cascade keys on this id, never the name, so renaming a tag in Cove cannot break its rule
    /// and two case variants of one name cannot route to two destination trees.
    /// </remarks>
    public Dictionary<int, Destination> TagDestinations { get; init; } = [];

    /// <summary>
    /// Per-entity-kind settings; a kind with no entry is renamed, with the global folder template and
    /// root.
    /// </summary>
    public Dictionary<RenamerFileKind, KindOptions> Kinds { get; init; } = [];

    /// <summary>Whether <paramref name="kind"/> is renamed at all; an unlisted kind is.</summary>
    /// <remarks>
    /// A null entry reads as unlisted. <c>{"Kinds":{"Text":null}}</c> is valid JSON that deserializes
    /// to a present key with no value, and the store's non-null restore does not reach inside a
    /// collection, so the value survives to here.
    /// </remarks>
    public bool IsKindEnabled(RenamerFileKind kind)
        => !Kinds.TryGetValue(kind, out var settings) || settings is null || settings.Enabled;

    /// <summary>
    /// The kind's own default destination, or <c>null</c> when it has none and the global
    /// <see cref="FolderRoot"/>/<see cref="FolderTemplate"/> pair applies.
    /// </summary>
    public Destination? KindDestination(RenamerFileKind kind)
        => Kinds.TryGetValue(kind, out var settings) ? settings?.Destination : null;

    /// <summary>Source-path routing rules, in user order.</summary>
    /// <remarks>
    /// Within the source-path category the resolver tries exact rules before regex rules. A regex
    /// pattern is bounded as <see cref="PathDestinationRule"/> describes.
    /// </remarks>
    public List<PathDestinationRule> PathDestinations { get; init; } = [];

    /// <summary>Stable tag ids whose items are excluded from rename and move.</summary>
    /// <remarks>
    /// Keyed on the id like <see cref="TagDestinations"/>. Excludes are evaluated before any routing
    /// category and surface as a visible <c>SkipExcluded</c> in the preview.
    /// </remarks>
    public List<int> ExcludeTagIds { get; init; } = [];

    /// <summary>Stable studio ids whose items are excluded from rename and move.</summary>
    /// <remarks>
    /// An item is excluded when its own <c>StudioId</c> or any of its <c>ParentStudios</c> ancestor ids
    /// is in this set. Keyed on the id like <see cref="StudioDestinations"/>, and evaluated before any
    /// routing category.
    /// </remarks>
    public List<int> ExcludeStudioIds { get; init; } = [];

    /// <summary>Source-path exclude rules, in user order.</summary>
    /// <remarks>
    /// Evaluated before any routing category. A regex pattern is bounded as <see cref="ExcludeRule"/>
    /// describes.
    /// </remarks>
    public List<ExcludeRule> ExcludePaths { get; init; } = [];

    /// <summary>
    /// The route for an item whose <c>Organized</c> flag is false, or <c>null</c> when there is no
    /// unorganized route.
    /// </summary>
    /// <remarks>
    /// Resolved before the tag, studio and path cascade, and it overrides <see cref="OnlyOrganized"/>
    /// for unorganized items, so an unorganized route is never nullified by the only-organized gate. The
    /// nullability carries a distinction the emptiness of a <see cref="Destination"/> cannot:
    /// <c>null</c> is "there is no unorganized route", while a present destination naming neither root
    /// nor folder is a route that moves nothing, and only the first falls through to the gate.
    /// </remarks>
    public Destination? UnorganizedDestination { get; init; }

    /// <summary>
    /// The bytes left free on each destination volume beyond the projected file bytes before a
    /// cross-drive batch is allowed to proceed.
    /// </summary>
    /// <remarks>
    /// The free-space guard adds this to a volume's summed need before comparing against its available
    /// space, so a batch never fills a disk to the brim. Same-volume renames are excluded from the sum,
    /// so this margin gates only cross-drive moves.
    /// </remarks>
    public long FreeSpaceHeadroomBytes { get; init; } = 1L << 30;

    /// <summary>
    /// The maximum number of simultaneous cross-drive transfers within one source-destination disk pair.
    /// </summary>
    /// <remarks>
    /// A batch runs its disk pairs one after another, so this value is also the batch's peak cross-drive
    /// concurrency and never the sum over the pairs it found. Same-volume renames are bounded separately
    /// by <see cref="SameVolumeConcurrency"/>.
    /// </remarks>
    public int CrossVolumeConcurrency { get; init; } = 2;

    /// <summary>
    /// The maximum number of simultaneous same-drive renames within one batch.
    /// </summary>
    /// <remarks>
    /// A same-drive rename is an instant metadata <c>File.Move</c> that consumes no extra space, so this
    /// is a pressure bound, not a space guard: it caps the in-flight <c>File.Move</c>, per-worker DB
    /// scope and event-bus operations a large selection would otherwise issue at once. The default is
    /// machine-independent, so the serialized default stays byte-identical across machines. A value
    /// &lt;= 0 is treated as unbounded.
    /// </remarks>
    public int SameVolumeConcurrency { get; init; } = 8;

    // Record value equality compares the list and dictionary members by reference, so a JSON round-trip
    // would never be equal. Equals and GetHashCode both run off EqualityComponents, and each collection
    // member is wrapped to compare by value: order-sensitive for lists, order-independent for the
    // destination maps, since a Dictionary has no guaranteed order and a round-trip may reorder keys.
    // The map's original key comparer is preserved.
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
        yield return StructuralEquality.Sequence(AssociatedExtensions);
        yield return StructuralEquality.Map(StudioDestinations, EqualityComparer<int>.Default);
        yield return StructuralEquality.Map(TagDestinations, EqualityComparer<int>.Default);
        yield return StructuralEquality.Map(Kinds, EqualityComparer<RenamerFileKind>.Default);
        yield return StructuralEquality.Sequence(PathDestinations);
        yield return StructuralEquality.Sequence(ExcludeTagIds);
        yield return StructuralEquality.Sequence(ExcludeStudioIds);
        yield return StructuralEquality.Sequence(ExcludePaths);
        yield return UnorganizedDestination;
        yield return FreeSpaceHeadroomBytes;
        yield return CrossVolumeConcurrency;
        yield return SameVolumeConcurrency;
    }

    /// <summary>Serializer settings shared by save and load, so the round-trip is symmetric.</summary>
    /// <remarks>
    /// Case-insensitive property names keep a hand-edited blob readable, and enums serialize as stable
    /// strings. <c>OptionsStore</c> reuses this exact instance.
    /// </remarks>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

// Drives a record's Equals and GetHashCode from one component list, so a member added to one can never
// be forgotten in the other. Collection members are wrapped to compare by value, which is what a JSON
// round-trip needs to stay equal to the original.
internal static class StructuralEquality
{
    public static bool Members(IEnumerable<object?> a, IEnumerable<object?> b) => a.SequenceEqual(b);

    public static int Hash(IEnumerable<object?> components)
    {
        var hc = new HashCode();
        foreach (var component in components)
        {
            hc.Add(component);
        }

        return hc.ToHashCode();
    }

    // Order-sensitive element compare.
    public static object Sequence<T>(IReadOnlyCollection<T> items) => new SeqKey<T>(items);

    // Order-independent compare under keyComparer; a round-trip may reorder a map's keys.
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

            // Built by assignment, so a key collision under the comparer keeps the last write.
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
            // Equals.
            int acc = 0;
            foreach (var kv in _map)
            {
                acc ^= HashCode.Combine(_keyComparer.GetHashCode(kv.Key), kv.Value);
            }

            return acc;
        }
    }
}
