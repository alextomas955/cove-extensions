using System.Text.RegularExpressions;

using Renamer.Options;

namespace Renamer.Planner;

// The routing classification for one entity, declared in the precedence order the DestinationResolver
// evaluates. The first category that produces a match wins.
public enum RouteCategory
{
    // An exclude rule matched; the planner treats this as a skip-with-reason.
    Excluded,

    // The item's Organized flag is false and an unorganized destination is configured; resolved before
    // the cascade.
    Unorganized,

    // A tag rule matched on a stable tag id, in entity tag-list order.
    Tag,

    // A studio rule matched on the stable StudioId or a parent-studio id; a direct match outranks an
    // ancestor.
    Studio,

    // A source-path rule matched, exact before regex.
    SourcePath,

    // No rule matched, so the item takes the default destination. RouteResult.Destination is null for
    // this category: no rule supplied one, and the default is the planner's to read.
    Unmatched,
}

// The result of routing one entity: the winning category, a short human label for the preview and the
// log ("Tag:anime", "Studio:42(direct)", "SourcePath:exact", "Default"), and the one destination that
// decides where the item lands. Destination is null for Unmatched, which takes the default instead, and
// for Excluded, which is never rendered at all.
//
// One lookup, one destination. The planner asks where a file goes exactly once and renders a single
// answer, which is what makes a destination stable under the move it names: two user-authored folder
// expressions are never joined, so no run can append one to the other.
public sealed record RouteResult(RouteCategory Category, string MatchedRule, Destination? Destination);

// The per-batch routing lookups, hoisted once per batch and handed to the pure DestinationResolver so
// it never re-walks or re-parses per entity. Built by the planner from RenamerOptions; the resolver only
// reads it.
//
// The studio, tag and exclude sets are keyed on stable ids, never on names. The exact path maps are
// built with DestinationResolver.SourcePathComparer over NormalizeSourcePath keys. The regex lists
// arrive compiled and validated once at build time with a match timeout applied there to bound ReDoS,
// so an invalid user pattern never reaches here and the resolver only calls IsMatch. A null or empty
// exclude member means none is configured.
//
// A studio exclude matches the entity's own StudioId or any of its ParentStudios ancestor ids. The
// exclude regexes carry no destination, because an excluded item is never moved.
public sealed record RouteLookups(
    IReadOnlyDictionary<int, Destination> StudioIdToDest,
    IReadOnlyDictionary<int, Destination> TagIdToDest,
    IReadOnlyDictionary<string, Destination> PathExactToDest,
    IReadOnlyList<(Regex Pattern, Destination Dest)> PathRegexRules,
    IReadOnlySet<int>? ExcludeTagIds = null,
    IReadOnlySet<int>? ExcludeStudioIds = null,
    IReadOnlySet<string>? ExcludePathsExact = null,
    IReadOnlyList<Regex>? ExcludePathRegex = null);
