using System.Text.RegularExpressions;
using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Tests.Planner;

public sealed class DestinationResolverTests
{
    // Tag ids are written out rather than derived from the names, because routing keys on the id and a
    // name-derived id would let a name comparison pass for an id comparison.
    private const int Anime = 7;
    private const int First = 1;
    private const int Second = 2;
    private const int Keep = 3;

    private static RenamerEntity Entity(
        bool organized = true,
        int? studioId = null,
        IReadOnlyList<(int Id, string Name)>? parentStudios = null,
        IReadOnlyList<(int Id, string Name)>? tags = null,
        string? studioName = null,
        string parentFolderPath = "media/in")
        => new(
            EntityId: 1, Kind: RenamerFileKind.Video, Title: "T", Code: null,
            StudioName: studioName, Date: null, Organized: organized,
            Performers: [], TagRefs: tags ?? [],
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

    private static Dictionary<int, Destination> RootsById(params (int Id, string Root)[] entries)
        => entries.ToDictionary(e => e.Id, e => new Destination { Root = e.Root });

    private static HashSet<string> PathSet(params string[] paths)
        => new(paths, DestinationResolver.SourcePathComparer);

    private static Regex Pattern(string pattern)
        => new(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));

    // A pattern that compiles, so the build-time guard admits it, and backtracks past its timeout on
    // Evil.
    private static Regex Redos() => new("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(50));

    private static readonly string Evil = new string('a', 40) + "!";

    [Fact]
    public void Unorganized_OutranksTagAndStudio()
    {
        var e = Entity(organized: false, studioId: 42, tags: [(Anime, "anime")]);
        var lk = Lookups(studios: RootsById((42, "S:42")), tags: RootsById((Anime, "T:anime")));
        var o = new RenamerOptions { UnorganizedDestination = new Destination { Root = "U:dest" } };

        var r = DestinationResolver.Resolve(e, o, lk);

        Assert.Equal(RouteCategory.Unorganized, r.Category);
        Assert.Equal("U:dest", r.Destination?.Root);
    }

    [Fact]
    public void Tag_OutranksStudioAndSourcePath()
    {
        var e = Entity(studioId: 42, tags: [(Anime, "anime")], parentFolderPath: "media/raw");
        var lk = Lookups(
            studios: RootsById((42, "S:42")),
            tags: RootsById((Anime, "T:anime")),
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
            studios: RootsById((42, "S:42")),
            pathExact: new Dictionary<string, Destination>(StringComparer.Ordinal) { ["media/raw"] = new Destination { Root = "P:raw" } });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Studio, r.Category);
        Assert.Equal("S:42", r.Destination?.Root);
    }

    [Fact]
    public void WithinTagCategory_FirstTagInEntityListOrderWins()
    {
        var e = Entity(tags: [(First, "first"), (Second, "second")]);
        var lk = Lookups(tags: RootsById((First, "T:first"), (Second, "T:second")));

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Tag, r.Category);
        Assert.Equal("T:first", r.Destination?.Root);
        Assert.Equal("Tag:first", r.MatchedRule);
    }

    [Fact]
    public void DirectStudio_OutranksAncestorStudio()
    {
        var e = Entity(studioId: 42, parentStudios: [(7, "Parent")]);
        var lk = Lookups(studios: RootsById((42, "S:42"), (7, "S:7")));

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Studio, r.Category);
        Assert.Equal("S:42", r.Destination?.Root);
        Assert.Equal("Studio:42(direct)", r.MatchedRule);
    }

    [Fact]
    public void AncestorOnly_TakesNearestAncestor()
    {
        // ParentStudios is nearest-first.
        var e = Entity(studioId: 42, parentStudios: [(7, "Near"), (3, "Far")]);
        var lk = Lookups(studios: RootsById((7, "S:7"), (3, "S:3")));

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Studio, r.Category);
        Assert.Equal("S:7", r.Destination?.Root);
        Assert.Equal("Studio:7(ancestor)", r.MatchedRule);
    }

    [Fact]
    public void TwoNameVariantsOfOneStudioId_ResolveToOneDestination()
    {
        var lk = Lookups(studios: RootsById((42, "S:42")));

        var a = Entity(studioId: 42, studioName: "Reality Kings", parentFolderPath: "x");
        var b = Entity(studioId: 42, studioName: "RealityKings", parentFolderPath: "y");

        Assert.Equal("S:42", DestinationResolver.Resolve(a, new RenamerOptions(), lk).Destination?.Root);
        Assert.Equal("S:42", DestinationResolver.Resolve(b, new RenamerOptions(), lk).Destination?.Root);
    }

    [Fact]
    public void TagRule_MatchesById_WhateverTheTagIsNowCalled()
    {
        var e = Entity(tags: [(Anime, "ANIME renamed")]);
        var lk = Lookups(tags: RootsById((Anime, "T:anime")));

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Tag, r.Category);
        Assert.Equal("T:anime", r.Destination?.Root);
    }

    [Fact]
    public void AnEntityWithNoTags_NeverMatchesATagRule()
    {
        var lk = Lookups(tags: RootsById((Anime, "T:anime")));

        var r = DestinationResolver.Resolve(Entity(), new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Unmatched, r.Category);
        Assert.Null(r.Destination);
    }

    // Windows and macOS treat two spellings that differ only in case as one folder, and Linux does not.
    [Fact]
    public void AnExactSourcePathRule_MatchesACaseVariant_OnlyWhereTheFilesystemIgnoresCase()
    {
        var options = new RenamerOptions
        {
            PathDestinations =
            [
                new PathDestinationRule { Pattern = "Media/Raw", Dest = new Destination { Root = "P:exact" } },
            ],
        };
        var lk = RouteLookups.From(options, (_, _) => { });

        var r = DestinationResolver.Resolve(Entity(parentFolderPath: "media/raw"), options, lk);

        bool caseInsensitivePlatform = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        Assert.Equal(caseInsensitivePlatform, r.Category == RouteCategory.SourcePath);
    }

    [Fact]
    public void ExactSourcePath_BeatsRegex()
    {
        var lk = Lookups(
            pathExact: new Dictionary<string, Destination>(StringComparer.Ordinal)
            {
                ["media/raw"] = new Destination { Root = "P:exact" },
            },
            pathRegex: [(Pattern("^media/"), new Destination { Root = "P:regex" })]);

        var r = DestinationResolver.Resolve(Entity(parentFolderPath: "media/raw"), new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.SourcePath, r.Category);
        Assert.Equal("P:exact", r.Destination?.Root);
        Assert.Equal("SourcePath:exact", r.MatchedRule);
    }

    [Fact]
    public void RegexOnly_StillRoutes()
    {
        var lk = Lookups(pathRegex: [(Pattern(@"^media/raw/\d+$"), new Destination { Root = "P:regex" })]);

        var r = DestinationResolver.Resolve(Entity(parentFolderPath: "media/raw/2024"), new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.SourcePath, r.Category);
        Assert.Equal("P:regex", r.Destination?.Root);
        Assert.Equal("SourcePath:regex", r.MatchedRule);
    }

    [Fact]
    public void BacktrackingRoutingRegex_TimesOut_LeavesTheItemUndecided_NotTheDefault()
    {
        var lk = Lookups(pathRegex: [(Redos(), new Destination { Root = "P:never" })]);

        var r = DestinationResolver.Resolve(Entity(parentFolderPath: Evil), new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.RuleTimedOut, r.Category);
        Assert.Equal("SourcePath:regex:^(a+)+$", r.MatchedRule);
        Assert.Null(r.Destination);
    }

    [Fact]
    public void UnorganizedItem_WithoutUnorganizedDestination_FallsThrough()
    {
        var r = DestinationResolver.Resolve(Entity(organized: false), new RenamerOptions(), Lookups());

        Assert.Equal(RouteCategory.Unmatched, r.Category);
    }

    [Fact]
    public void UnmatchedItem_CarriesNoDestination_AndIsLabelledDefault()
    {
        var o = new RenamerOptions { FolderRoot = "D:dest", FolderTemplate = "$studio" };

        var r = DestinationResolver.Resolve(Entity(), o, Lookups());

        Assert.Equal(RouteCategory.Unmatched, r.Category);
        Assert.Equal("Default", r.MatchedRule);
        Assert.Null(r.Destination);
    }

    [Fact]
    public void ExcludeByTag_Exact_ReturnsExcluded()
    {
        var e = Entity(tags: [(Anime, "anime")]);
        var lk = Lookups(excludeTags: new HashSet<int> { Anime });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
        Assert.Equal("Exclude:Tag:anime", r.MatchedRule);
        Assert.Null(r.Destination);
    }

    [Fact]
    public void ExcludeByTag_SurvivesARename_AndTheReasonShowsTheNewName()
    {
        // The reason reads the entity's tag name, so it follows a rename instead of showing the bare id.
        var e = Entity(tags: [(11, "Japanese Animation")]);
        var lk = Lookups(excludeTags: new HashSet<int> { 11 });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
        Assert.Equal("Exclude:Tag:Japanese Animation", r.MatchedRule);
        Assert.Null(r.Destination);
    }

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
        var e = Entity(studioId: 42, parentStudios: [(7, "Parent")]);
        var lk = Lookups(excludeStudios: new HashSet<int> { 7 });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
        Assert.Equal("Exclude:Studio:7(ancestor)", r.MatchedRule);
    }

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
        var e = Entity(parentFolderPath: "media/protected/");
        var lk = Lookups(excludePathsExact: PathSet("media/protected"));

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
    }

    [Fact]
    public void ExcludeByPath_Regex_ReturnsExcluded()
    {
        var e = Entity(parentFolderPath: "media/keep/2024");
        var lk = Lookups(excludePathRegex: [Pattern(@"^media/keep/\d+$")]);

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
        Assert.Equal("Exclude:Path:regex", r.MatchedRule);
    }

    [Fact]
    public void Exclude_BeatsAMatchingTagRoute()
    {
        var e = Entity(tags: [(Anime, "anime")]);
        var lk = Lookups(tags: RootsById((Anime, "T:anime")), excludeTags: new HashSet<int> { Anime });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
    }

    [Fact]
    public void Exclude_BeatsAMatchingStudioRoute()
    {
        var e = Entity(studioId: 42);
        var lk = Lookups(studios: RootsById((42, "S:42")), excludeStudios: new HashSet<int> { 42 });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
    }

    [Fact]
    public void Exclude_BeatsUnorganized()
    {
        var e = Entity(organized: false, tags: [(Anime, "anime")]);
        var o = new RenamerOptions { UnorganizedDestination = new Destination { Root = "U:dest" } };
        var lk = Lookups(excludeTags: new HashSet<int> { Anime });

        var r = DestinationResolver.Resolve(e, o, lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
    }

    [Fact]
    public void NoExcludeMatch_FallsThroughToRoutingUnchanged()
    {
        var e = Entity(tags: [(Keep, "keep")]);
        var lk = Lookups(tags: RootsById((Keep, "T:keep")), excludeTags: new HashSet<int> { Anime });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Tag, r.Category);
        Assert.Equal("T:keep", r.Destination?.Root);
    }

    [Fact]
    public void ExcludeRegex_Backtracking_TimesOut_LeavesTheItemUndecided_NotRenamed()
    {
        var lk = Lookups(excludePathRegex: [Redos()]);

        var r = DestinationResolver.Resolve(Entity(parentFolderPath: Evil), new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.RuleTimedOut, r.Category);
        Assert.Equal("Exclude:Path:regex:^(a+)+$", r.MatchedRule);
    }
}
