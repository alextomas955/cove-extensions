using System.Text.RegularExpressions;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// Pure unit tests for <see cref="DestinationResolver.Resolve"/> — no DB, no disk. Proves the
/// locked routing precedence (Excludes → Unorganized → Tag → Studio → Source-path →
/// Unmatched), within-category list order, direct-outranks-ancestor, route-on-stable-id for
/// both studios and tags, source-path exact-beats-regex, and the unorganized slot.
/// </summary>
[Trait("Tier", "L0")]
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
        IReadOnlyList<(int Id, string Name)>? tagRefs = null,
        string? studioName = null,
        string parentFolderPath = "media/in")
        => new(
            EntityId: 1, Kind: RenamerFileKind.Video, Title: "T", Code: null,
            StudioName: studioName, Date: null, Organized: organized,
            Performers: [], TagRefs: tagRefs ?? [],
            Files: [File(parentFolderPath)],
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

    private static Dictionary<int, Destination> TagMap(params (int id, string dest)[] entries)
    {
        var d = new Dictionary<int, Destination>();
        foreach (var (id, dest) in entries)
        {
            d[id] = Dests.At(dest);
        }

        return d;
    }

    // --- precedence matrix ----------------------------------------------------------------------

    [Fact]
    public void Unorganized_OutranksTagAndStudio()
    {
        // Unorganized + tag + studio all "match": Unorganized wins (runs before the cascade).
        var e = Entity(organized: false, studioId: 42, tagRefs: [(11, "anime")]);
        var lk = Lookups(
            studios: new Dictionary<int, Destination> { [42] = Dests.At("S:42") },
            tags: TagMap((11, "T:anime")));
        var o = new RenamerOptions { UnorganizedDestination = Dests.At("U:dest") };

        var r = DestinationResolver.Resolve(e, o, lk);

        Assert.Equal(RouteCategory.Unorganized, r.Category);
        Assert.Equal(Dests.At("U:dest"), r.Destination);
    }

    [Fact]
    public void Tag_OutranksStudioAndSourcePath()
    {
        // Tag + studio + source-path all match: Tag wins (higher category).
        var e = Entity(studioId: 42, tagRefs: [(11, "anime")], parentFolderPath: "media/raw");
        var lk = Lookups(
            studios: new Dictionary<int, Destination> { [42] = Dests.At("S:42") },
            tags: TagMap((11, "T:anime")),
            pathExact: new Dictionary<string, Destination>(StringComparer.Ordinal) { ["media/raw"] = Dests.At("P:raw") });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Tag, r.Category);
        Assert.Equal(Dests.At("T:anime"), r.Destination);
    }

    [Fact]
    public void Studio_OutranksSourcePath()
    {
        var e = Entity(studioId: 42, parentFolderPath: "media/raw");
        var lk = Lookups(
            studios: new Dictionary<int, Destination> { [42] = Dests.At("S:42") },
            pathExact: new Dictionary<string, Destination>(StringComparer.Ordinal) { ["media/raw"] = Dests.At("P:raw") });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Studio, r.Category);
        Assert.Equal(Dests.At("S:42"), r.Destination);
    }

    [Fact]
    public void WithinTagCategory_FirstTagInEntityListOrderWins()
    {
        // Both tag IDS have a rule; the entity lists tag 1 before tag 2 → tag 1 wins. The rule map is
        // built in the OPPOSITE order to prove the winner comes from the entity's tag order, not from
        // the map's insertion order.
        var e = Entity(tagRefs: [(1, "first"), (2, "second")]);
        var lk = Lookups(tags: TagMap((2, "T:second"), (1, "T:first")));

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Tag, r.Category);
        Assert.Equal(Dests.At("T:first"), r.Destination);
        Assert.Equal("Tag:first", r.MatchedRule);
    }

    // --- direct outranks ancestor ---------------------------------------------------------------

    [Fact]
    public void DirectStudio_OutranksAncestorStudio()
    {
        // Both the direct id (42) and an ancestor id (7) have rules → the direct rule wins.
        var e = Entity(studioId: 42, parentStudios: [(7, "Parent")]);
        var lk = Lookups(studios: new Dictionary<int, Destination> { [42] = Dests.At("S:42"), [7] = Dests.At("S:7") });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Studio, r.Category);
        Assert.Equal(Dests.At("S:42"), r.Destination);
        Assert.Equal("Studio:42(direct)", r.MatchedRule);
    }

    [Fact]
    public void AncestorOnly_TakesNearestAncestor()
    {
        // No direct rule; ParentStudios is nearest-first [(7),(3)] and both have rules → 7 wins.
        var e = Entity(studioId: 42, parentStudios: [(7, "Near"), (3, "Far")]);
        var lk = Lookups(studios: new Dictionary<int, Destination> { [7] = Dests.At("S:7"), [3] = Dests.At("S:3") });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Studio, r.Category);
        Assert.Equal(Dests.At("S:7"), r.Destination);
        Assert.Equal("Studio:7(ancestor)", r.MatchedRule);
    }
}

/// <summary>Route-on-stable-id: the studio NAME never affects the match.</summary>
[Trait("Tier", "L0")]
public sealed class DestinationResolverRouteOnStableStudioIdTests
{
    [Fact]
    public void TwoNameVariantsOfOneStudioId_ResolveToOneDestination()
    {
        var lk = new RouteLookups(
            new Dictionary<int, Destination> { [42] = Dests.At("S:42") },
            new Dictionary<int, Destination>(),
            new Dictionary<string, Destination>(StringComparer.Ordinal),
            []);

        var a = new RenamerEntity(1, RenamerFileKind.Video, "A", null, "Reality Kings", null, true,
            [], [], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, "x")], StudioId: 42);
        var b = new RenamerEntity(2, RenamerFileKind.Video, "B", null, "RealityKings", null, true,
            [], [], [new RenamerFile(2, RenamerFileKind.Video, "b.mkv", 1, "y")], StudioId: 42);

        var ra = DestinationResolver.Resolve(a, new RenamerOptions(), lk);
        var rb = DestinationResolver.Resolve(b, new RenamerOptions(), lk);

        Assert.Equal(Dests.At("S:42"), ra.Destination);
        Assert.Equal(Dests.At("S:42"), rb.Destination);
        Assert.Equal(ra.Destination, rb.Destination);
    }
}

/// <summary>
/// Route-on-stable-tag-id: the tag NAME never affects the match, but it is still the text the
/// preview shows. Both halves matter — the first is why the migration off names happened, the second
/// is what the migration must not cost the user.
/// </summary>
[Trait("Tier", "L0")]
public sealed class DestinationResolverTagRoutingTests
{
    private static RouteLookups TagLookups(IReadOnlyDictionary<int, Destination> tags)
        => new(new Dictionary<int, Destination>(), tags,
               new Dictionary<string, Destination>(StringComparer.Ordinal), []);

    private static RenamerEntity TaggedEntity(params (int Id, string Name)[] tagRefs)
        => new(1, RenamerFileKind.Video, "T", null, null, null, true,
               [], tagRefs,
               [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, "x")]);

    [Fact]
    public void TwoNameVariantsOfOneTagId_ResolveToOneDestination()
    {
        // The rename the id migration exists for: the SAME tag id spelled two different ways (a
        // rename, a case variant) routes to one destination. Under name-keying the renamed one would
        // have silently stopped matching.
        var lk = TagLookups(new Dictionary<int, Destination> { [11] = Dests.At("T:anime") });

        var before = DestinationResolver.Resolve(TaggedEntity((11, "anime")), new RenamerOptions(), lk);
        var after = DestinationResolver.Resolve(TaggedEntity((11, "Japanese Animation")), new RenamerOptions(), lk);

        Assert.Equal(Dests.At("T:anime"), before.Destination);
        Assert.Equal(Dests.At("T:anime"), after.Destination);
        Assert.Equal(before.Destination, after.Destination);
    }

    [Fact]
    public void TagRoutedById_ReasonStringNamesTheTag_NotItsId()
    {
        // The preview's route reason is user-visible text. Matching moved to the id; the label must
        // still read the tag's CURRENT name, so a user reading a preview sees "Tag:anime", never
        // "Tag:11". Nothing else in the suite pins this.
        var lk = TagLookups(new Dictionary<int, Destination> { [11] = Dests.At("T:anime") });

        var r = DestinationResolver.Resolve(TaggedEntity((11, "anime")), new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Tag, r.Category);
        Assert.Equal("Tag:anime", r.MatchedRule);
        Assert.DoesNotContain("11", r.MatchedRule);
    }

    [Fact]
    public void TagRoutedById_AfterARename_ReasonStringShowsTheNewName()
    {
        // The name is read off the entity, not off the stored rule, so the label follows a rename.
        var lk = TagLookups(new Dictionary<int, Destination> { [11] = Dests.At("T:anime") });

        var r = DestinationResolver.Resolve(
            TaggedEntity((11, "Japanese Animation")), new RenamerOptions(), lk);

        Assert.Equal("Tag:Japanese Animation", r.MatchedRule);
    }

    [Fact]
    public void AnEntityWithNoTags_NeverMatchesATagRule()
    {
        var lk = TagLookups(new Dictionary<int, Destination> { [11] = Dests.At("T:anime") });

        var r = DestinationResolver.Resolve(TaggedEntity(), new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Unmatched, r.Category);
    }
}

/// <summary>Source-path routing: exact beats regex; a regex-only match still routes.</summary>
[Trait("Tier", "L0")]
public sealed class DestinationResolverSourcePathRoutingTests
{
    private static RenamerEntity AtPath(string path)
        => new(1, RenamerFileKind.Video, "T", null, null, null, true,
               [], [], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, path)]);

    [Fact]
    public void ExactSourcePath_BeatsRegex()
    {
        var exact = new Dictionary<string, Destination>(StringComparer.Ordinal) { ["media/raw"] = Dests.At("P:exact") };
        var regex = new List<(Regex, Destination)>
        {
            (new Regex("^media/", RegexOptions.None, TimeSpan.FromSeconds(1)), Dests.At("P:regex")),
        };
        var lk = new RouteLookups(
            new Dictionary<int, Destination>(),
            new Dictionary<int, Destination>(),
            exact, regex);

        var r = DestinationResolver.Resolve(AtPath("media/raw"), new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.SourcePath, r.Category);
        Assert.Equal(Dests.At("P:exact"), r.Destination);
        Assert.Equal("SourcePath:exact", r.MatchedRule);
    }

    [Fact]
    public void RegexOnly_StillRoutes()
    {
        var regex = new List<(Regex, Destination)>
        {
            (new Regex(@"^media/raw/\d+$", RegexOptions.None, TimeSpan.FromSeconds(1)), Dests.At("P:regex")),
        };
        var lk = new RouteLookups(
            new Dictionary<int, Destination>(),
            new Dictionary<int, Destination>(),
            new Dictionary<string, Destination>(StringComparer.Ordinal), regex);

        var r = DestinationResolver.Resolve(AtPath("media/raw/2024"), new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.SourcePath, r.Category);
        Assert.Equal(Dests.At("P:regex"), r.Destination);
        Assert.Equal("SourcePath:regex", r.MatchedRule);
    }
}

/// <summary>
/// A valid-but-backtracking source-path regex must be treated as "no match" (skip the rule,
/// keep cascading) when it times out at match time — NEVER an uncaught throw that aborts the batch.
/// The build-time guard only catches a SYNTAX-invalid pattern (ArgumentException); a pattern that
/// compiles fine then exhibits catastrophic backtracking throws RegexMatchTimeoutException at IsMatch
/// time, which the resolver now catches and falls through.
/// </summary>
[Trait("Tier", "L0")]
public sealed class DestinationResolverRegexTimeoutTests
{
    private static RenamerEntity AtPath(string path)
        => new(1, RenamerFileKind.Video, "T", null, null, null, true,
               [], [], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, path)]);

    [Fact]
    public void BacktrackingRegex_TimesOut_FallsThroughToNextCascadeStage_NotThrow()
    {
        // Classic ReDoS pattern + a long non-matching input → catastrophic backtracking. A tiny
        // match timeout makes the test fast and deterministic. The pattern COMPILES fine (no
        // ArgumentException), so the build-time guard would have admitted it.
        var redos = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(50));
        string evil = new string('a', 40) + "!";   // never matches → forces the backtracking blowup

        // The timing-out regex is the FIRST source-path rule; a second, benign exact rule for the SAME
        // path proves the cascade keeps going after the timeout (exact is tried before regex, so to
        // exercise the regex-timeout fall-through we set ONLY the regex rule and assert Unmatched).
        var lk = new RouteLookups(
            new Dictionary<int, Destination>(),
            new Dictionary<int, Destination>(),
            new Dictionary<string, Destination>(StringComparer.Ordinal),
            [(redos, Dests.At("P:never"))]);

        // Must NOT throw, and the timed-out rule must NOT match → fall through to unmatched.
        var r = DestinationResolver.Resolve(AtPath(evil), new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Unmatched, r.Category);
        Assert.Null(r.Destination);
    }

    [Fact]
    public void BacktrackingRegex_TimesOut_LaterStudioRuleStillWins_BatchContinues()
    {
        // The timing-out source-path regex sits in the cascade, but a STUDIO rule (higher precedence)
        // matches first — proving a routed item still routes and the timeout never aborts resolution.
        // (Studio outranks source-path, so the studio rule is reached before the regex; this asserts
        // the resolver returns cleanly with the studio route regardless of a pathological path rule.)
        var redos = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(50));
        var e = new RenamerEntity(1, RenamerFileKind.Video, "T", null, null, null, true,
            [], [], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, new string('a', 40) + "!")],
            StudioId: 42);
        var lk = new RouteLookups(
            new Dictionary<int, Destination> { [42] = Dests.At("S:42") },
            new Dictionary<int, Destination>(),
            new Dictionary<string, Destination>(StringComparer.Ordinal),
            [(redos, Dests.At("P:never"))]);

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Studio, r.Category);
        Assert.Equal(Dests.At("S:42"), r.Destination);
    }
}

/// <summary>Unorganized items route to the unorganized destination, not skipped.</summary>
[Trait("Tier", "L0")]
public sealed class DestinationResolverUnorganizedRouteTests
{
    [Fact]
    public void UnorganizedItem_RoutesToUnorganizedDestination()
    {
        var e = new RenamerEntity(1, RenamerFileKind.Video, "T", null, null, null, Organized: false,
            [], [], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, "x")]);
        var o = new RenamerOptions { UnorganizedDestination = Dests.At("U:dest") };

        var r = DestinationResolver.Resolve(e, o, new RouteLookups(
            new Dictionary<int, Destination>(),
            new Dictionary<int, Destination>(),
            new Dictionary<string, Destination>(StringComparer.Ordinal), []));

        Assert.Equal(RouteCategory.Unorganized, r.Category);
        Assert.Equal(Dests.At("U:dest"), r.Destination);
    }

    [Fact]
    public void UnorganizedItem_WithoutUnorganizedDestination_FallsThrough()
    {
        // No unorganized destination set → the unorganized slot does NOT fire; falls to unmatched.
        var e = new RenamerEntity(1, RenamerFileKind.Video, "T", null, null, null, Organized: false,
            [], [], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, "x")]);

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), new RouteLookups(
            new Dictionary<int, Destination>(),
            new Dictionary<int, Destination>(),
            new Dictionary<string, Destination>(StringComparer.Ordinal), []));

        Assert.Equal(RouteCategory.Unmatched, r.Category);
    }
}

/// <summary>
/// EXCL-01/02/03: excludes run FIRST in the resolver — a matching tag / studio (incl.
/// parent, stable id) / source-path (exact + regex) returns <see cref="RouteCategory.Excluded"/>
/// BEFORE any routing category (including Unorganized) is considered, with a clear label. A
/// match-time ReDoS timeout on an exclude regex is treated as no-match (classify-not-throw), never
/// aborting resolution. PURE — no DB, no disk.
/// </summary>
[Trait("Tier", "L0")]
public sealed class DestinationResolverExcludeTests
{
    private static RenamerEntity Entity(
        bool organized = true,
        int? studioId = null,
        IReadOnlyList<(int Id, string Name)>? parentStudios = null,
        IReadOnlyList<(int Id, string Name)>? tagRefs = null,
        string parentFolderPath = "media/in")
        => new(
            EntityId: 1, Kind: RenamerFileKind.Video, Title: "T", Code: null,
            StudioName: null, Date: null, Organized: organized,
            Performers: [], TagRefs: tagRefs ?? [],
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

    private static HashSet<int> TagSet(params int[] ids) => [.. ids];

    private static HashSet<string> PathSet(params string[] paths)
        => new(paths, DestinationResolver.SourcePathComparer);

    // --- EXCL-01: tag ---------------------------------------------------------------------------

    [Fact]
    public void ExcludeByTag_Exact_ReturnsExcluded()
    {
        var e = Entity(tagRefs: [(11, "anime")]);
        var lk = Lookups(excludeTags: TagSet(11));

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
        Assert.Equal("Exclude:Tag:anime", r.MatchedRule);
        Assert.Null(r.Destination);
    }

    [Fact]
    public void ExcludeByTag_SurvivesARename_AndTheReasonShowsTheNewName()
    {
        // The exclude is keyed on the id, so renaming the tag keeps the item excluded — and the
        // user-visible reason follows the rename rather than degrading to the bare id.
        var e = Entity(tagRefs: [(11, "Japanese Animation")]);
        var lk = Lookups(excludeTags: TagSet(11));

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
        Assert.Equal("Exclude:Tag:Japanese Animation", r.MatchedRule);
    }

    // --- EXCL-02: studio (direct + ancestor, stable id) -----------------------------------------

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
        // EXCL-02 "studio OR its parent": the direct studio (42) is NOT excluded, but a parent (7) is.
        var e = Entity(studioId: 42, parentStudios: [(7, "Parent")]);
        var lk = Lookups(excludeStudios: new HashSet<int> { 7 });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
        Assert.Equal("Exclude:Studio:7(ancestor)", r.MatchedRule);
    }

    // --- EXCL-03: source-path (exact + regex) ---------------------------------------------------

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

    // --- precedence: excludes beat routes AND Unorganized ---------------------------------------

    [Fact]
    public void Exclude_BeatsAMatchingTagRoute()
    {
        // The SAME tag id carries BOTH a destination rule and an exclude rule. The exclude wins (it
        // runs first) and reports its OWN reason — the two outcomes must not collapse into one, so
        // the label is the exclude's, no destination is carried, and the route reason is absent.
        var e = Entity(tagRefs: [(11, "anime")]);
        var lk = Lookups(
            tags: new Dictionary<int, Destination> { [11] = Dests.At("T:anime") },
            excludeTags: TagSet(11));

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
        Assert.Equal("Exclude:Tag:anime", r.MatchedRule);
        Assert.NotEqual("Tag:anime", r.MatchedRule);
        Assert.Null(r.Destination);
    }

    [Fact]
    public void Exclude_BeatsAMatchingStudioRoute()
    {
        // Studio 42 is both a route and an exclude → excluded.
        var e = Entity(studioId: 42);
        var lk = Lookups(
            studios: new Dictionary<int, Destination> { [42] = Dests.At("S:42") },
            excludeStudios: new HashSet<int> { 42 });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
    }

    [Fact]
    public void Exclude_BeatsUnorganized()
    {
        // An unorganized item that matches an exclude is Excluded, NOT routed to the unorganized dest.
        var e = Entity(organized: false, tagRefs: [(11, "anime")]);
        var o = new RenamerOptions { UnorganizedDestination = Dests.At("U:dest") };
        var lk = Lookups(excludeTags: TagSet(11));

        var r = DestinationResolver.Resolve(e, o, lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
    }

    [Fact]
    public void NoExcludeMatch_FallsThroughToRoutingUnchanged()
    {
        // An entity whose tag is NOT excluded still routes normally (additive / non-breaking).
        var e = Entity(tagRefs: [(12, "keep")]);
        var lk = Lookups(
            tags: new Dictionary<int, Destination> { [12] = Dests.At("T:keep") },
            excludeTags: TagSet(11));

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Tag, r.Category);
        Assert.Equal(Dests.At("T:keep"), r.Destination);
    }

    [Fact]
    public void NullExcludeLookups_BehaveAsEmpty_NoRegression()
    {
        // The legacy 4-arg lookups (exclude params default null) must never exclude anything.
        var e = Entity(studioId: 42, tagRefs: [(11, "anime")], parentFolderPath: "media/protected");
        var lk = new RouteLookups(
            new Dictionary<int, Destination>(),
            new Dictionary<int, Destination>(),
            new Dictionary<string, Destination>(StringComparer.Ordinal),
            []);

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Unmatched, r.Category);
    }

    // --- ReDoS: match-time timeout on an exclude regex = no-match (classify-not-throw) -----------

    [Fact]
    public void ExcludeRegex_Backtracking_TimesOut_TreatedAsNoMatch_NotThrow()
    {
        // Classic ReDoS pattern + a long non-matching path → catastrophic backtracking. A tiny match
        // timeout makes it fast/deterministic. The timeout must be a NO-MATCH (the item is NOT
        // excluded by that rule) and must NOT throw — so resolution completes as Unmatched.
        var redos = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(50));
        string evil = new string('a', 40) + "!";
        var e = Entity(parentFolderPath: evil);
        var lk = Lookups(excludePathRegex: [redos]);

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Unmatched, r.Category);
    }
}
