using System.Text.RegularExpressions;

namespace Renamer.Planner;

/// <summary>
/// The routing classification for one entity, in the locked precedence order the
/// <c>DestinationResolver</c> evaluates: <c>Excludes → Unorganized → Tag → Studio (incl. parent)
/// → Source-path → Default</c>, with <see cref="SourceConfine"/> as the no-route fallback. The
/// first category that produces a match wins.
/// </summary>
public enum RouteCategory
{
    /// <summary>An exclude rule matched — the planner treats this as a skip-with-reason.</summary>
    Excluded,

    /// <summary>The item's <c>Organized</c> flag is false and an unorganized destination is configured; resolved before the cascade.</summary>
    Unorganized,

    /// <summary>A tag-name rule matched, case-insensitively, in entity tag-list order.</summary>
    Tag,

    /// <summary>A studio rule matched on the stable <c>StudioId</c> or a parent-studio id; a direct match outranks an ancestor.</summary>
    Studio,

    /// <summary>A source-path rule matched, exact before regex.</summary>
    SourcePath,

    /// <summary>
    /// The GATED default-relocate: an unmatched item routed to the configured default root. Reachable
    /// ONLY when <c>EnableDefaultRelocate</c> is true — the flag ships off because a runaway
    /// default-relocate has whole-library blast radius and volume-aware undo is the recovery path.
    /// </summary>
    Default,

    /// <summary>
    /// No route matched (or default-relocate is disabled): the file keeps its OWN parent-folder
    /// anchor — the legacy, non-relocating behavior. <see cref="RouteResult.DestinationRootTemplate"/>
    /// is null for this category.
    /// </summary>
    SourceConfine,
}

/// <summary>
/// The result of routing one entity: the matched <see cref="Category"/>, a short human label for
/// the preview/log, and the destination-root template the planner anchors the move against.
/// </summary>
/// <param name="Category">The winning precedence category.</param>
/// <param name="MatchedRule">
/// A short human label for preview/log: e.g. <c>"Tag:anime"</c>, <c>"Studio:42(direct)"</c>,
/// <c>"Studio:7(ancestor)"</c>, <c>"SourcePath:exact"</c>, <c>"SourcePath:regex"</c>,
/// <c>"Unorganized"</c>, <c>"Default"</c>, <c>"Exclude"</c>, <c>"InPlace"</c>.
/// </param>
/// <param name="DestinationRootTemplate">
/// The absolute destination-root template to anchor the move against, or <c>null</c> for
/// <see cref="RouteCategory.SourceConfine"/>/<see cref="RouteCategory.Excluded"/> (the planner then
/// keeps the file's own <c>ParentFolderPath</c> anchor).
/// </param>
public sealed record RouteResult(RouteCategory Category, string MatchedRule, string? DestinationRootTemplate);

/// <summary>
/// The per-batch routing lookups, hoisted ONCE per batch and handed to the pure
/// <c>DestinationResolver</c> so it never re-walks/re-parses per entity. Built by the planner from
/// <c>RenamerOptions</c>; this is a pure model — the resolver only reads it.
/// </summary>
/// <param name="StudioIdToDest">Stable studio id → destination-root template (keyed on the id, not the name).</param>
/// <param name="TagNameToDest">Tag name → destination-root template; built with <c>StringComparer.OrdinalIgnoreCase</c>.</param>
/// <param name="PathExactToDest">Exact source-path → destination-root template; tried before the regex rules.</param>
/// <param name="PathRegexRules">
/// The pre-parsed source-path regex rules, in user order: each <c>Pattern</c> is compiled and
/// validated ONCE at build time (NOT <c>RegexOptions.Compiled</c> — overkill for a short batch),
/// with a match timeout applied there to bound ReDoS; the resolver only calls <c>IsMatch</c>.
/// An invalid user regex is rejected at build time, so it never reaches here.
/// </param>
/// <param name="ExcludeTagNames">
/// Exact tag-name exclude set, built case-insensitively (<see cref="StringComparer.OrdinalIgnoreCase"/>),
/// mirroring <see cref="TagNameToDest"/>'s comparer. An entity carrying any tag in this set is excluded
/// FIRST (before every routing category). Empty (the default) = no tag excludes.
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
    IReadOnlyDictionary<int, string> StudioIdToDest,
    IReadOnlyDictionary<string, string> TagNameToDest,
    IReadOnlyDictionary<string, string> PathExactToDest,
    IReadOnlyList<(Regex Pattern, string Dest)> PathRegexRules,
    IReadOnlySet<string>? ExcludeTagNames = null,
    IReadOnlySet<int>? ExcludeStudioIds = null,
    IReadOnlySet<string>? ExcludePathsExact = null,
    IReadOnlyList<Regex>? ExcludePathRegex = null);
