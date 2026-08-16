using System.Text.RegularExpressions;
using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// The two shapes a <see cref="Destination"/> comes in, spelled short enough to sit inside a fixture's
/// collection initializer without hiding what it says.
/// </summary>
/// <remarks>
/// A helper rather than an implicit conversion from <c>string</c>, deliberately: the whole point of the
/// current model is that a destination names a root CHOSEN from Cove's library paths, and a conversion
/// would let a bare path string mean either half of it depending on where it was written.
/// </remarks>
internal static class Dests
{
    /// <summary>A destination rooted at a chosen library path, optionally with a relative template under it.</summary>
    internal static Destination At(string root, string template = "") => new() { Root = root, Template = template };

    /// <summary>A destination measured from the library path containing the file.</summary>
    internal static Destination Own(string template) => new() { Template = template };
}

/// <summary>
/// The routing-neutral <see cref="RouteLookups"/> the suite shares — no destination maps, no regex
/// rules, no excludes — for every test whose subject is not routing.
/// </summary>
/// <remarks>
/// With no rules the resolver always returns <see cref="RouteCategory.Unmatched"/>, so each file
/// anchors on its own parent folder. The planner used to supply this internally through a non-routing
/// overload, which let a test say nothing about routing while still depending on it; passing this
/// value makes "this test does not route" something the call site states. Shared as a single instance
/// exactly as the planner's own field was: every collection is empty and nothing here is written.
/// </remarks>
internal static class RouteLookupsFixtures
{
    /// <summary>Empty lookups: every map empty, every exclude set absent.</summary>
    internal static RouteLookups RoutingNeutral { get; } = new(
        StudioIdToDest: new Dictionary<int, Destination>(),
        TagIdToDest: new Dictionary<int, Destination>(),
        PathExactToDest: new Dictionary<string, Destination>(StringComparer.Ordinal),
        PathRegexRules: Array.Empty<(Regex, Destination)>());
}
