using System.Text.RegularExpressions;
using Renamer.Planner;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// The routing-neutral <see cref="RouteLookups"/> the suite shares — no destination maps, no regex
/// rules, no excludes — for every test whose subject is not routing.
/// </summary>
/// <remarks>
/// With no rules the resolver always returns <see cref="RouteCategory.SourceConfine"/>, so each file
/// anchors on its own parent folder. The planner used to supply this internally through a non-routing
/// overload, which let a test say nothing about routing while still depending on it; passing this
/// value makes "this test does not route" something the call site states. Shared as a single instance
/// exactly as the planner's own field was: every collection is empty and nothing here is written.
/// </remarks>
internal static class RouteLookupsFixtures
{
    /// <summary>Empty lookups: every map empty, every exclude set absent.</summary>
    internal static RouteLookups RoutingNeutral { get; } = new(
        StudioIdToDest: new Dictionary<int, string>(),
        TagIdToDest: new Dictionary<int, string>(),
        PathExactToDest: new Dictionary<string, string>(StringComparer.Ordinal),
        PathRegexRules: Array.Empty<(Regex, string)>());
}
