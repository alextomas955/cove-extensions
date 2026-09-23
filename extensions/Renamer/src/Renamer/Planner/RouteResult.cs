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

    // A source-path regex, routing or exclude, exceeded its match timeout.
    RuleTimedOut,

    // No rule matched, so the item takes the default destination. RouteResult.Destination is null for
    // this category: no rule supplied one, and the default is the planner's to read.
    Unmatched,
}

// The result of routing one entity: the winning category, a short human label for the preview and the
// log ("Tag:anime", "Studio:42(direct)", "SourcePath:exact", "Default"), and the one destination that
// decides where the item lands. Destination is null for Unmatched, which takes the default instead, and
// for Excluded and RuleTimedOut, which are never rendered at all.
//
// One lookup, one destination. The planner asks where a file goes exactly once and renders a single
// answer, which is what makes a destination stable under the move it names: two user-authored folder
// expressions are never joined, so no run can append one to the other.
public sealed record RouteResult(RouteCategory Category, string MatchedRule, Destination? Destination);
