using System.Text.RegularExpressions;
using Renamer.Options;
using Renamer.Planner;

using static Renamer.Tests.Planner.TagFixtures;

namespace Renamer.Tests.Planner;

internal static class TagFixtures
{
    internal static int TagId(string name) => StringComparer.OrdinalIgnoreCase.GetHashCode(name) & 0xFFFFF;

    internal static IReadOnlyList<(int Id, string Name)> TagRefs(IReadOnlyList<string> names)
        => [.. names.Select(n => (TagId(n), n))];

    internal static Dictionary<int, Destination> TagMap(params (string name, string dest)[] entries)
    {
        var d = new Dictionary<int, Destination>();
        foreach (var (name, dest) in entries)
        {
            d[TagId(name)] = new Destination { Root = dest };
        }

        return d;
    }

    internal static HashSet<int> TagSet(params string[] names) => [.. names.Select(TagId)];
}

public sealed class DestinationResolverPrecedenceTests
{
    // --- builders -------------------------------------------------------------------------------

    private static RenamerFile File(string parentFolderPath = "media/in")
        => new(FileId: 1, Kind: RenamerFileKind.Video, Basename: "clip.mkv",
               ParentFolderId: 1, ParentFolderPath: parentFolderPath);

    private static RenamerEntity Entity(
        bool organized = true,
        int? studioId = null,
        IReadOnlyList<(int Id, string Name)>? parentStudios = null,
        IReadOnlyList<string>? tags = null,
        string? studioName = null,
        string parentFolderPath = "media/in")
        => new(
            EntityId: 1, Kind: RenamerFileKind.Video, Title: "T", Code: null,
            StudioName: studioName, Date: null, Organized: organized,
            Performers: [], TagRefs: TagRefs(tags ?? []), Files: [File(parentFolderPath)],
            StudioId: studioId, ParentStudios: parentStudios);

    private static RouteLookups Lookups(
        IReadOnlyDictionary<int, Destination>? studios = null,
        IReadOnlyDictionary<int, Destination>? tags = null,
        IReadOnlyDictionary<string, Destination>? pathExact = null,
        IReadOnlyList<(Regex, Destination)>? pathRegex = null,
        IReadOnlySet<int>? excludeTags = null,
        IReadOnlySet<int>? excludeStudios = null,
        IReadOnlySet<string>? excludePathsExact = null,
        IReadOnlyList<Regex>? excludePathRegex = null)
        => new(
            studios ?? new Dictionary<int, Destination>(),
            tags ?? new Dictionary<int, Destination>(),
            pathExact ?? new Dictionary<string, Destination>(StringComparer.Ordinal),
            pathRegex ?? [],
            excludeTags,
            excludeStudios,
            excludePathsExact,
            excludePathRegex);

    // --- precedence matrix ----------------------------------------------------------------------

    [Fact]
    public void Unorganized_OutranksTagAndStudio()
    {
        // Unorganized + tag + studio all "match": Unorganized wins (runs before the cascade).
        var e = Entity(organized: false, studioId: 42, tags: ["anime"]);
        var lk = Lookups(
            studios: new Dictionary<int, Destination> { [42] = new Destination { Root = "S:42" } },
            tags: TagMap(("anime", "T:anime")));
        var o = new RenamerOptions { UnorganizedDestination = new Destination { Root = "U:dest" } };

        var r = DestinationResolver.Resolve(e, o, lk);

        Assert.Equal(RouteCategory.Unorganized, r.Category);
        Assert.Equal("U:dest", r.Destination?.Root);
    }

    [Fact]
    public void Tag_OutranksStudioAndSourcePath()
    {
        // Tag + studio + source-path all match: Tag wins (higher category).
        var e = Entity(studioId: 42, tags: ["anime"], parentFolderPath: "media/raw");
        var lk = Lookups(
            studios: new Dictionary<int, Destination> { [42] = new Destination { Root = "S:42" } },
            tags: TagMap(("anime", "T:anime")),
            pathExact: new Dictionary<string, Destination>(StringComparer.Ordinal) { ["media/raw"] = new Destination { Root = "P:raw" } });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Tag, r.Category);
        Assert.Equal("T:anime", r.Destination?.Root);
    }

    [Fact]
    public void Studio_OutranksSourcePath()
    {
        var e = Entity(studioId: 42, parentFolderPath: "media/raw");
        var lk = Lookups(
            studios: new Dictionary<int, Destination> { [42] = new Destination { Root = "S:42" } },
            pathExact: new Dictionary<string, Destination>(StringComparer.Ordinal) { ["media/raw"] = new Destination { Root = "P:raw" } });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Studio, r.Category);
        Assert.Equal("S:42", r.Destination?.Root);
    }

    [Fact]
    public void WithinTagCategory_FirstTagInEntityListOrderWins()
    {
        // Both tags have a rule; the entity lists "first" before "second" → first wins.
        var e = Entity(tags: ["first", "second"]);
        var lk = Lookups(tags: TagMap(("first", "T:first"), ("second", "T:second")));

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Tag, r.Category);
        Assert.Equal("T:first", r.Destination?.Root);
        Assert.Equal("Tag:first", r.MatchedRule);
    }

    // --- direct outranks ancestor ---------------------------------------------------------------

    [Fact]
    public void DirectStudio_OutranksAncestorStudio()
    {
        // Both the direct id (42) and an ancestor id (7) have rules → the direct rule wins.
        var e = Entity(studioId: 42, parentStudios: [(7, "Parent")]);
        var lk = Lookups(studios: new Dictionary<int, Destination> { [42] = new Destination { Root = "S:42" }, [7] = new Destination { Root = "S:7" } });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Studio, r.Category);
        Assert.Equal("S:42", r.Destination?.Root);
        Assert.Equal("Studio:42(direct)", r.MatchedRule);
    }

    [Fact]
    public void AncestorOnly_TakesNearestAncestor()
    {
        // No direct rule; ParentStudios is nearest-first [(7),(3)] and both have rules → 7 wins.
        var e = Entity(studioId: 42, parentStudios: [(7, "Near"), (3, "Far")]);
        var lk = Lookups(studios: new Dictionary<int, Destination> { [7] = new Destination { Root = "S:7" }, [3] = new Destination { Root = "S:3" } });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Studio, r.Category);
        Assert.Equal("S:7", r.Destination?.Root);
        Assert.Equal("Studio:7(ancestor)", r.MatchedRule);
    }
}

public sealed class DestinationResolverRouteOnStableStudioIdTests
{
    [Fact]
    public void TwoNameVariantsOfOneStudioId_ResolveToOneDestination()
    {
        var lk = new RouteLookups(
            new Dictionary<int, Destination> { [42] = new Destination { Root = "S:42" } },
            new Dictionary<int, Destination>(),
            new Dictionary<string, Destination>(StringComparer.Ordinal),
            []);

        var a = new RenamerEntity(1, RenamerFileKind.Video, "A", null, "Reality Kings", null, true,
            [], [], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, "x")], StudioId: 42);
        var b = new RenamerEntity(2, RenamerFileKind.Video, "B", null, "RealityKings", null, true,
            [], [], [new RenamerFile(2, RenamerFileKind.Video, "b.mkv", 1, "y")], StudioId: 42);

        var ra = DestinationResolver.Resolve(a, new RenamerOptions(), lk);
        var rb = DestinationResolver.Resolve(b, new RenamerOptions(), lk);

        Assert.Equal("S:42", ra.Destination?.Root);
        Assert.Equal("S:42", rb.Destination?.Root);
        Assert.Equal(ra.Destination, rb.Destination);
    }
}

public sealed class DestinationResolverTagRoutingTests
{
    [Fact]
    public void TagRule_MatchesById_WhateverTheTagIsNowCalled()
    {
        var tags = new Dictionary<int, Destination> { [7] = new Destination { Root = "T:anime" } };
        var lk = new RouteLookups(
            new Dictionary<int, Destination>(), tags,
            new Dictionary<string, Destination>(StringComparer.Ordinal), []);

        var e = new RenamerEntity(1, RenamerFileKind.Video, "T", null, null, null, true,
            [], [(7, "ANIME renamed")], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, "x")]);

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Tag, r.Category);
        Assert.Equal("T:anime", r.Destination?.Root);
    }

    [Fact]
    public void AnEntityWithNoTags_NeverMatchesATagRule()
    {
        var lk = new RouteLookups(
            new Dictionary<int, Destination>(),
            new Dictionary<int, Destination> { [7] = new Destination { Root = "T:anime" } },
            new Dictionary<string, Destination>(StringComparer.Ordinal), []);

        var e = new RenamerEntity(1, RenamerFileKind.Video, "T", null, null, null, true,
            [], [], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, "x")]);

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Unmatched, r.Category);
        Assert.Null(r.Destination);
    }
}

public sealed class DestinationResolverSourcePathRoutingTests
{
    private static RenamerEntity AtPath(string path)
        => new(1, RenamerFileKind.Video, "T", null, null, null, true,
               [], [], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, path)]);

    [Fact]
    public void ExactSourcePath_BeatsRegex()
    {
        var exact = new Dictionary<string, Destination>(StringComparer.Ordinal)
        {
            ["media/raw"] = new Destination { Root = "P:exact" },
        };
        var regex = new List<(Regex, Destination)>
        {
            (new Regex("^media/", RegexOptions.None, TimeSpan.FromSeconds(1)), new Destination { Root = "P:regex" }),
        };
        var lk = new RouteLookups(
            new Dictionary<int, Destination>(),
            new Dictionary<int, Destination>(),
            exact, regex);

        var r = DestinationResolver.Resolve(AtPath("media/raw"), new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.SourcePath, r.Category);
        Assert.Equal("P:exact", r.Destination?.Root);
        Assert.Equal("SourcePath:exact", r.MatchedRule);
    }

    [Fact]
    public void RegexOnly_StillRoutes()
    {
        var regex = new List<(Regex, Destination)>
        {
            (new Regex(@"^media/raw/\d+$", RegexOptions.None, TimeSpan.FromSeconds(1)), new Destination { Root = "P:regex" }),
        };
        var lk = new RouteLookups(
            new Dictionary<int, Destination>(),
            new Dictionary<int, Destination>(),
            new Dictionary<string, Destination>(StringComparer.Ordinal), regex);

        var r = DestinationResolver.Resolve(AtPath("media/raw/2024"), new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.SourcePath, r.Category);
        Assert.Equal("P:regex", r.Destination?.Root);
        Assert.Equal("SourcePath:regex", r.MatchedRule);
    }
}

public sealed class DestinationResolverRegexTimeoutTests
{
    private static RenamerEntity AtPath(string path)
        => new(1, RenamerFileKind.Video, "T", null, null, null, true,
               [], [], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, path)]);

    [Fact]
    public void BacktrackingRoutingRegex_TimesOut_LeavesTheItemUndecided_NotTheDefault()
    {
        // The pattern compiles, so the build-time guard admits it, and backtracks past its timeout on
        // this input.
        var redos = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(50));
        string evil = new string('a', 40) + "!";
        var lk = new RouteLookups(
            new Dictionary<int, Destination>(),
            new Dictionary<int, Destination>(),
            new Dictionary<string, Destination>(StringComparer.Ordinal),
            [(redos, new Destination { Root = "P:never" })]);

        var r = DestinationResolver.Resolve(AtPath(evil), new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.RuleTimedOut, r.Category);
        Assert.Equal("SourcePath:regex:^(a+)+$", r.MatchedRule);
        Assert.Null(r.Destination);
    }

    [Fact]
    public void BacktrackingRegex_TimesOut_LaterStudioRuleStillWins_BatchContinues()
    {
        // The timing-out source-path regex sits in the cascade, but a studio rule (higher precedence)
        // matches first - proving a routed item still routes and the timeout never aborts resolution.
        // (Studio outranks source-path, so the studio rule is reached before the regex; this asserts
        // the resolver returns cleanly with the studio route regardless of a pathological path rule.)
        var redos = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(50));
        var e = new RenamerEntity(1, RenamerFileKind.Video, "T", null, null, null, true,
            [], [], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, new string('a', 40) + "!")],
            StudioId: 42);
        var lk = new RouteLookups(
            new Dictionary<int, Destination> { [42] = new Destination { Root = "S:42" } },
            new Dictionary<int, Destination>(),
            new Dictionary<string, Destination>(StringComparer.Ordinal),
            [(redos, new Destination { Root = "P:never" })]);

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Studio, r.Category);
        Assert.Equal("S:42", r.Destination?.Root);
    }
}

public sealed class DestinationResolverUnorganizedRouteTests
{
    [Fact]
    public void UnorganizedItem_RoutesToUnorganizedDestination()
    {
        var e = new RenamerEntity(1, RenamerFileKind.Video, "T", null, null, null, Organized: false,
            [], [], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, "x")]);
        var o = new RenamerOptions { UnorganizedDestination = new Destination { Root = "U:dest" } };

        var r = DestinationResolver.Resolve(e, o, new RouteLookups(
            new Dictionary<int, Destination>(),
            new Dictionary<int, Destination>(),
            new Dictionary<string, Destination>(StringComparer.Ordinal), []));

        Assert.Equal(RouteCategory.Unorganized, r.Category);
        Assert.Equal("U:dest", r.Destination?.Root);
    }

    [Fact]
    public void UnorganizedItem_WithoutUnorganizedDestination_FallsThrough()
    {
        // No unorganized destination set → the unorganized slot does not fire; falls to source-confine.
        var e = new RenamerEntity(1, RenamerFileKind.Video, "T", null, null, null, Organized: false,
            [], [], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, "x")]);

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), new RouteLookups(
            new Dictionary<int, Destination>(),
            new Dictionary<int, Destination>(),
            new Dictionary<string, Destination>(StringComparer.Ordinal), []));

        Assert.Equal(RouteCategory.Unmatched, r.Category);
    }
}

public sealed class DestinationResolverUnmatchedTests
{
    private static RenamerEntity Unmatched()
        => new(1, RenamerFileKind.Video, "T", null, null, null, true,
               [], [], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, "media/in")]);

    private static RouteLookups Empty()
        => new(new Dictionary<int, Destination>(),
               new Dictionary<int, Destination>(),
               new Dictionary<string, Destination>(StringComparer.Ordinal), []);

    [Fact]
    public void UnmatchedItem_CarriesNoDestination_AndIsLabelledDefault()
    {
        var o = new RenamerOptions { FolderRoot = "D:dest", FolderTemplate = "$studio" };

        var r = DestinationResolver.Resolve(Unmatched(), o, Empty());

        Assert.Equal(RouteCategory.Unmatched, r.Category);
        Assert.Equal("Default", r.MatchedRule);
        Assert.Null(r.Destination);
    }
}

public sealed class DestinationResolverExcludeTests
{
    private static RenamerEntity Entity(
        bool organized = true,
        int? studioId = null,
        IReadOnlyList<(int Id, string Name)>? parentStudios = null,
        IReadOnlyList<string>? tags = null,
        string parentFolderPath = "media/in")
        => new(
            EntityId: 1, Kind: RenamerFileKind.Video, Title: "T", Code: null,
            StudioName: null, Date: null, Organized: organized,
            Performers: [], TagRefs: TagRefs(tags ?? []),
            Files: [new RenamerFile(1, RenamerFileKind.Video, "clip.mkv", 1, parentFolderPath)],
            StudioId: studioId, ParentStudios: parentStudios);

    private static RouteLookups Lookups(
        IReadOnlyDictionary<int, Destination>? studios = null,
        IReadOnlyDictionary<int, Destination>? tags = null,
        IReadOnlyDictionary<string, Destination>? pathExact = null,
        IReadOnlyList<(Regex, Destination)>? pathRegex = null,
        IReadOnlySet<int>? excludeTags = null,
        IReadOnlySet<int>? excludeStudios = null,
        IReadOnlySet<string>? excludePathsExact = null,
        IReadOnlyList<Regex>? excludePathRegex = null)
        => new(
            studios ?? new Dictionary<int, Destination>(),
            tags ?? new Dictionary<int, Destination>(),
            pathExact ?? new Dictionary<string, Destination>(StringComparer.Ordinal),
            pathRegex ?? [],
            excludeTags, excludeStudios, excludePathsExact, excludePathRegex);

    private static HashSet<string> PathSet(params string[] paths)
        => new(paths, DestinationResolver.SourcePathComparer);

    // --- tag ---------------------------------------------------------------------------

    [Fact]
    public void ExcludeByTag_Exact_ReturnsExcluded()
    {
        var e = Entity(tags: ["anime"]);
        var lk = Lookups(excludeTags: TagSet("anime"));

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
        Assert.Equal("Exclude:Tag:anime", r.MatchedRule);
        Assert.Null(r.Destination);
    }

    [Fact]
    public void ExcludeByTag_CaseInsensitive()
    {
        // Entity tag "Anime", exclude set keyed "anime" → OrdinalIgnoreCase matches.
        var e = Entity(tags: ["Anime"]);
        var lk = Lookups(excludeTags: TagSet("anime"));

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
    }

    [Fact]
    public void ExcludeByTag_SurvivesARename_AndTheReasonShowsTheNewName()
    {
        // The id is written out rather than derived from the name, because the name is what varies:
        // a rule stored against tag 11 keeps excluding after the tag is renamed. The reason string
        // reads the entity, so it follows the rename instead of degrading to the bare id.
        var e = new RenamerEntity(
            EntityId: 1, Kind: RenamerFileKind.Video, Title: "T", Code: null,
            StudioName: null, Date: null, Organized: true,
            Performers: [], TagRefs: [(11, "Japanese Animation")],
            Files: [new RenamerFile(1, RenamerFileKind.Video, "clip.mkv", 1, "media/in")]);
        var lk = Lookups(excludeTags: new HashSet<int> { 11 });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
        Assert.Equal("Exclude:Tag:Japanese Animation", r.MatchedRule);
        Assert.Null(r.Destination);
    }

    // --- studio (direct + ancestor, stable id) -----------------------------------------

    [Fact]
    public void ExcludeByStudio_DirectId_ReturnsExcluded()
    {
        var e = Entity(studioId: 42);
        var lk = Lookups(excludeStudios: new HashSet<int> { 42 });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
        Assert.Equal("Exclude:Studio:42(direct)", r.MatchedRule);
    }

    [Fact]
    public void ExcludeByStudio_AncestorId_ReturnsExcluded()
    {
        // "studio or its parent": the direct studio (42) is not excluded, but a parent (7) is.
        var e = Entity(studioId: 42, parentStudios: [(7, "Parent")]);
        var lk = Lookups(excludeStudios: new HashSet<int> { 7 });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
        Assert.Equal("Exclude:Studio:7(ancestor)", r.MatchedRule);
    }

    // --- source-path (exact + regex) ---------------------------------------------------

    [Fact]
    public void ExcludeByPath_Exact_ReturnsExcluded()
    {
        var e = Entity(parentFolderPath: "media/protected");
        var lk = Lookups(excludePathsExact: PathSet("media/protected"));

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
        Assert.Equal("Exclude:Path:exact", r.MatchedRule);
    }

    [Fact]
    public void ExcludeByPath_Exact_TrailingSlashNormalized()
    {
        // NormalizeSourcePath trims a trailing slash on the stored path before lookup.
        var e = Entity(parentFolderPath: "media/protected/");
        var lk = Lookups(excludePathsExact: PathSet("media/protected"));

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
    }

    [Fact]
    public void ExcludeByPath_Regex_ReturnsExcluded()
    {
        var e = Entity(parentFolderPath: "media/keep/2024");
        var lk = Lookups(excludePathRegex:
            [new Regex(@"^media/keep/\d+$", RegexOptions.None, TimeSpan.FromSeconds(1))]);

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
        Assert.Equal("Exclude:Path:regex", r.MatchedRule);
    }

    // --- precedence: excludes beat routes and Unorganized ---------------------------------------

    [Fact]
    public void Exclude_BeatsAMatchingTagRoute()
    {
        // The same tag is both a route and an exclude → the exclude wins (runs first).
        var e = Entity(tags: ["anime"]);
        var lk = Lookups(
            tags: TagMap(("anime", "T:anime")),
            excludeTags: TagSet("anime"));

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
    }

    [Fact]
    public void Exclude_BeatsAMatchingStudioRoute()
    {
        // Studio 42 is both a route and an exclude → excluded.
        var e = Entity(studioId: 42);
        var lk = Lookups(
            studios: new Dictionary<int, Destination> { [42] = new Destination { Root = "S:42" } },
            excludeStudios: new HashSet<int> { 42 });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
    }

    [Fact]
    public void Exclude_BeatsUnorganized()
    {
        // An unorganized item that matches an exclude is Excluded, not routed to the unorganized dest.
        var e = Entity(organized: false, tags: ["anime"]);
        var o = new RenamerOptions { UnorganizedDestination = new Destination { Root = "U:dest" } };
        var lk = Lookups(excludeTags: TagSet("anime"));

        var r = DestinationResolver.Resolve(e, o, lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
    }

    [Fact]
    public void NoExcludeMatch_FallsThroughToRoutingUnchanged()
    {
        // An entity whose tag is not excluded still routes normally (additive / non-breaking).
        var e = Entity(tags: ["keep"]);
        var lk = Lookups(
            tags: TagMap(("keep", "T:keep")),
            excludeTags: TagSet("anime"));

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Tag, r.Category);
        Assert.Equal("T:keep", r.Destination?.Root);
    }

    [Fact]
    public void NullExcludeLookups_BehaveAsEmpty_NoRegression()
    {
        // The legacy 4-arg lookups (exclude params default null) must never exclude anything.
        var e = Entity(studioId: 42, tags: ["anime"], parentFolderPath: "media/protected");
        var lk = new RouteLookups(
            new Dictionary<int, Destination>(),
            new Dictionary<int, Destination>(),
            new Dictionary<string, Destination>(StringComparer.Ordinal),
            []);

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Unmatched, r.Category);
    }

    [Fact]
    public void ExcludeRegex_Backtracking_TimesOut_LeavesTheItemUndecided_NotRenamed()
    {
        var redos = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(50));
        string evil = new string('a', 40) + "!";
        var e = Entity(parentFolderPath: evil);
        var lk = Lookups(excludePathRegex: [redos]);

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.RuleTimedOut, r.Category);
        Assert.Equal("Exclude:Path:regex:^(a+)+$", r.MatchedRule);
    }
}
