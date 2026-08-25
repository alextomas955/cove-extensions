using System.Text.RegularExpressions;

using Renamer.Options;

namespace Renamer.Planner;

/// <summary>
/// The routing classification for one entity, in the locked precedence order the
/// <c>DestinationResolver</c> evaluates: <c>Excludes → Unorganized → Tag → Studio (incl. parent)
/// → Source-path</c>, with <see cref="Unmatched"/> as the no-rule fallback. The
/// first category that produces a match wins.
/// </summary>
public enum RouteCategory
{
    /// <summary>An exclude rule matched — the planner treats this as a skip-with-reason.</summary>
    Excluded,

    /// <summary>The item's <c>Organized</c> flag is false and an unorganized destination is configured; resolved before the cascade.</summary>
    Unorganized,

    /// <summary>A tag rule matched on a stable tag id, in entity tag-list order.</summary>
    Tag,

    /// <summary>A studio rule matched on the stable <c>StudioId</c> or a parent-studio id; a direct match outranks an ancestor.</summary>
    Studio,

    /// <summary>A source-path rule matched, exact before regex.</summary>
    SourcePath,

    /// <summary>
    /// No rule matched, so the item takes the DEFAULT destination
    /// (<c>RenamerOptions.FolderRoot</c> + <c>RenamerOptions.FolderTemplate</c>).
    /// <see cref="RouteResult.Destination"/> is null for this category: no rule supplied a
    /// destination, and the default is the planner's to read.
    /// </summary>
    Unmatched,
}

/// <summary>
/// The result of routing one entity: the matched <see cref="Category"/>, a short human label for
/// the preview/log, and the one destination that decides where the item lands.
/// </summary>
/// <param name="Category">The winning precedence category.</param>
/// <param name="MatchedRule">
/// A short human label for preview/log: e.g. <c>"Tag:anime"</c>, <c>"Studio:42(direct)"</c>,
/// <c>"Studio:7(ancestor)"</c>, <c>"SourcePath:exact"</c>, <c>"SourcePath:regex"</c>,
/// <c>"Unorganized"</c>, <c>"Exclude"</c>, <c>"Default"</c>.
/// </param>
/// <param name="Destination">
/// The matched rule's whole answer, root and relative template together, not a root some other
/// setting is then rendered underneath. <c>null</c> for
/// <see cref="RouteCategory.Unmatched"/>/<see cref="RouteCategory.Excluded"/>: an unmatched item
/// takes the default destination instead, and an excluded one is never rendered at all.
/// </param>
/// <remarks>
/// One lookup, one destination. The planner asks where a file goes exactly once and renders a single
/// answer, which is what makes a destination stable under the move it names: two user-authored
/// folder expressions are never joined, so no run can append one to the other.
/// </remarks>
public sealed record RouteResult(RouteCategory Category, string MatchedRule, Destination? Destination);

/// <summary>
/// The per-batch routing lookups, hoisted ONCE per batch and handed to the pure
/// <c>DestinationResolver</c> so it never re-walks/re-parses per entity. Built by the planner from
/// <c>RenamerOptions</c>; this is a pure model — the resolver only reads it.
/// </summary>
/// <param name="StudioIdToDest">Stable studio id → destination (keyed on the id, not the name).</param>
/// <param name="TagIdToDest">Stable tag id → destination (keyed on the id, not the name).</param>
/// <param name="PathExactToDest">Exact source-path → destination; tried before the regex rules.</param>
/// <param name="PathRegexRules">
/// The pre-parsed source-path regex rules, in user order: each <c>Pattern</c> is compiled and
/// validated ONCE at build time (NOT <c>RegexOptions.Compiled</c> — overkill for a short batch),
/// with a match timeout applied there to bound ReDoS; the resolver only calls <c>IsMatch</c>.
/// An invalid user regex is rejected at build time, so it never reaches here.
/// </param>
/// <param name="ExcludeTagIds">
/// Stable tag-id exclude set, keyed exactly like <see cref="TagIdToDest"/> and mirroring
/// <see cref="ExcludeStudioIds"/>. An entity carrying any tag in this set is excluded FIRST (before
/// every routing category). Empty (the default) = no tag excludes.
/// </param>
/// <param name="ExcludeStudioIds">
/// Stable studio-id exclude set. An entity is excluded when its own <c>StudioId</c> OR any of its
/// <c>ParentStudios</c> ancestor ids is in this set (keyed on the id, not the name). Empty = no studio excludes.
/// </param>
/// <param name="ExcludePathsExact">
/// Exact source-path exclude set, built with <see cref="DestinationResolver.SourcePathComparer"/>
/// (OS-aware) over <c>NormalizeSourcePath</c> keys, mirroring <see cref="PathExactToDest"/>. Empty = none.
/// </param>
/// <param name="ExcludePathRegex">
/// Pre-parsed source-path exclude regexes, in user order — compiled/validated ONCE at build time with
/// the same match timeout that bounds <see cref="PathRegexRules"/> (an invalid pattern is
/// skipped-with-a-log at build time and never reaches here). No destination is carried — an excluded
/// item is never moved. The resolver only calls <c>IsMatch</c>. Empty = none.
/// </param>
public sealed record RouteLookups(
    IReadOnlyDictionary<int, Destination> StudioIdToDest,
    IReadOnlyDictionary<int, Destination> TagIdToDest,
    IReadOnlyDictionary<string, Destination> PathExactToDest,
    IReadOnlyList<(Regex Pattern, Destination Dest)> PathRegexRules,
    IReadOnlySet<int>? ExcludeTagIds = null,
    IReadOnlySet<int>? ExcludeStudioIds = null,
    IReadOnlySet<string>? ExcludePathsExact = null,
    IReadOnlyList<Regex>? ExcludePathRegex = null);
