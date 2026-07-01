using System.Text.RegularExpressions;
using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Tests.Planner;

/// <summary>
/// Pure unit tests for <see cref="DestinationResolver.Resolve"/> — no DB, no disk. Proves the
/// locked routing precedence (Excludes → Unorganized → Tag → Studio → Source-path → Default →
/// SourceConfine), within-category list order, direct-outranks-ancestor, route-on-stable-id,
/// tag case-insensitivity, source-path exact-beats-regex, the unorganized slot, and the
/// default-relocate code guard (off → SourceConfine, on → Default).
/// </summary>
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
            Performers: [], Tags: tags ?? [], Files: [File(parentFolderPath)],
            StudioId: studioId, ParentStudios: parentStudios);

    private static RouteLookups Lookups(
        IReadOnlyDictionary<int, string>? studios = null,
        IReadOnlyDictionary<string, string>? tags = null,
        IReadOnlyDictionary<string, string>? pathExact = null,
        IReadOnlyList<(Regex, string)>? pathRegex = null,
        IReadOnlySet<string>? excludeTags = null,
        IReadOnlySet<int>? excludeStudios = null,
        IReadOnlySet<string>? excludePathsExact = null,
        IReadOnlyList<Regex>? excludePathRegex = null)
        => new(
            studios ?? new Dictionary<int, string>(),
            tags ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            pathExact ?? new Dictionary<string, string>(StringComparer.Ordinal),
            pathRegex ?? [],
            excludeTags,
            excludeStudios,
            excludePathsExact,
            excludePathRegex);

    private static Dictionary<string, string> TagMap(params (string name, string dest)[] entries)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, dest) in entries)
        {
            d[name] = dest;
        }

        return d;
    }

    // --- precedence matrix ----------------------------------------------------------------------

    [Fact]
    public void Unorganized_OutranksTagAndStudio()
    {
        // Unorganized + tag + studio all "match": Unorganized wins (runs before the cascade).
        var e = Entity(organized: false, studioId: 42, tags: ["anime"]);
        var lk = Lookups(
            studios: new Dictionary<int, string> { [42] = "S:42" },
            tags: TagMap(("anime", "T:anime")));
        var o = new RenamerOptions { UnorganizedDestination = "U:dest" };

        var r = DestinationResolver.Resolve(e, o, lk);

        Assert.Equal(RouteCategory.Unorganized, r.Category);
        Assert.Equal("U:dest", r.DestinationRootTemplate);
    }

    [Fact]
    public void Tag_OutranksStudioAndSourcePath()
    {
        // Tag + studio + source-path all match: Tag wins (higher category).
        var e = Entity(studioId: 42, tags: ["anime"], parentFolderPath: "media/raw");
        var lk = Lookups(
            studios: new Dictionary<int, string> { [42] = "S:42" },
            tags: TagMap(("anime", "T:anime")),
            pathExact: new Dictionary<string, string>(StringComparer.Ordinal) { ["media/raw"] = "P:raw" });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Tag, r.Category);
        Assert.Equal("T:anime", r.DestinationRootTemplate);
    }

    [Fact]
    public void Studio_OutranksSourcePath()
    {
        var e = Entity(studioId: 42, parentFolderPath: "media/raw");
        var lk = Lookups(
            studios: new Dictionary<int, string> { [42] = "S:42" },
            pathExact: new Dictionary<string, string>(StringComparer.Ordinal) { ["media/raw"] = "P:raw" });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Studio, r.Category);
        Assert.Equal("S:42", r.DestinationRootTemplate);
    }

    [Fact]
    public void WithinTagCategory_FirstTagInEntityListOrderWins()
    {
        // Both tags have a rule; the entity lists "first" before "second" → first wins.
        var e = Entity(tags: ["first", "second"]);
        var lk = Lookups(tags: TagMap(("first", "T:first"), ("second", "T:second")));

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Tag, r.Category);
        Assert.Equal("T:first", r.DestinationRootTemplate);
        Assert.Equal("Tag:first", r.MatchedRule);
    }

    // --- direct outranks ancestor ---------------------------------------------------------------

    [Fact]
    public void DirectStudio_OutranksAncestorStudio()
    {
        // Both the direct id (42) and an ancestor id (7) have rules → the direct rule wins.
        var e = Entity(studioId: 42, parentStudios: [(7, "Parent")]);
        var lk = Lookups(studios: new Dictionary<int, string> { [42] = "S:42", [7] = "S:7" });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Studio, r.Category);
        Assert.Equal("S:42", r.DestinationRootTemplate);
        Assert.Equal("Studio:42(direct)", r.MatchedRule);
    }

    [Fact]
    public void AncestorOnly_TakesNearestAncestor()
    {
        // No direct rule; ParentStudios is nearest-first [(7),(3)] and both have rules → 7 wins.
        var e = Entity(studioId: 42, parentStudios: [(7, "Near"), (3, "Far")]);
        var lk = Lookups(studios: new Dictionary<int, string> { [7] = "S:7", [3] = "S:3" });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Studio, r.Category);
        Assert.Equal("S:7", r.DestinationRootTemplate);
        Assert.Equal("Studio:7(ancestor)", r.MatchedRule);
    }
}

/// <summary>Route-on-stable-id: the studio NAME never affects the match (P7 / ROUTE-01).</summary>
public sealed class DestinationResolverRouteOnStableStudioIdTests
{
    [Fact]
    public void TwoNameVariantsOfOneStudioId_ResolveToOneDestination()
    {
        var lk = new RouteLookups(
            new Dictionary<int, string> { [42] = "S:42" },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.Ordinal),
            []);

        var a = new RenamerEntity(1, RenamerFileKind.Video, "A", null, "Reality Kings", null, true,
            [], [], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, "x")], StudioId: 42);
        var b = new RenamerEntity(2, RenamerFileKind.Video, "B", null, "RealityKings", null, true,
            [], [], [new RenamerFile(2, RenamerFileKind.Video, "b.mkv", 1, "y")], StudioId: 42);

        var ra = DestinationResolver.Resolve(a, new RenamerOptions(), lk);
        var rb = DestinationResolver.Resolve(b, new RenamerOptions(), lk);

        Assert.Equal("S:42", ra.DestinationRootTemplate);
        Assert.Equal("S:42", rb.DestinationRootTemplate);
        Assert.Equal(ra.DestinationRootTemplate, rb.DestinationRootTemplate);
    }
}

/// <summary>Tag routing is case-insensitive on the tag name (ROUTE-02).</summary>
public sealed class DestinationResolverTagRoutingTests
{
    [Fact]
    public void TagRule_MatchesCaseInsensitively()
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Anime"] = "T:anime" };
        var lk = new RouteLookups(
            new Dictionary<int, string>(), tags,
            new Dictionary<string, string>(StringComparer.Ordinal), []);

        var e = new RenamerEntity(1, RenamerFileKind.Video, "T", null, null, null, true,
            [], ["anime"], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, "x")]);

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Tag, r.Category);
        Assert.Equal("T:anime", r.DestinationRootTemplate);
    }
}

/// <summary>Source-path routing: exact beats regex; a regex-only match still routes (ROUTE-03).</summary>
public sealed class DestinationResolverSourcePathRoutingTests
{
    private static RenamerEntity AtPath(string path)
        => new(1, RenamerFileKind.Video, "T", null, null, null, true,
               [], [], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, path)]);

    [Fact]
    public void ExactSourcePath_BeatsRegex()
    {
        var exact = new Dictionary<string, string>(StringComparer.Ordinal) { ["media/raw"] = "P:exact" };
        var regex = new List<(Regex, string)>
        {
            (new Regex("^media/", RegexOptions.None, TimeSpan.FromSeconds(1)), "P:regex"),
        };
        var lk = new RouteLookups(
            new Dictionary<int, string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            exact, regex);

        var r = DestinationResolver.Resolve(AtPath("media/raw"), new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.SourcePath, r.Category);
        Assert.Equal("P:exact", r.DestinationRootTemplate);
        Assert.Equal("SourcePath:exact", r.MatchedRule);
    }

    [Fact]
    public void RegexOnly_StillRoutes()
    {
        var regex = new List<(Regex, string)>
        {
            (new Regex(@"^media/raw/\d+$", RegexOptions.None, TimeSpan.FromSeconds(1)), "P:regex"),
        };
        var lk = new RouteLookups(
            new Dictionary<int, string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.Ordinal), regex);

        var r = DestinationResolver.Resolve(AtPath("media/raw/2024"), new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.SourcePath, r.Category);
        Assert.Equal("P:regex", r.DestinationRootTemplate);
        Assert.Equal("SourcePath:regex", r.MatchedRule);
    }
}

/// <summary>
/// CR-02: a valid-but-backtracking source-path regex must be treated as "no match" (skip the rule,
/// keep cascading) when it times out at match time — NEVER an uncaught throw that aborts the batch.
/// The build-time guard only catches a SYNTAX-invalid pattern (ArgumentException); a pattern that
/// compiles fine then exhibits catastrophic backtracking throws RegexMatchTimeoutException at IsMatch
/// time, which the resolver now catches and falls through.
/// </summary>
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
        // exercise the regex-timeout fall-through we set ONLY the regex rule and assert SourceConfine).
        var lk = new RouteLookups(
            new Dictionary<int, string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.Ordinal),
            [(redos, "P:never")]);

        // Must NOT throw, and the timed-out rule must NOT match → fall through to source-confine.
        var r = DestinationResolver.Resolve(AtPath(evil), new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.SourceConfine, r.Category);
        Assert.Null(r.DestinationRootTemplate);
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
            new Dictionary<int, string> { [42] = "S:42" },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.Ordinal),
            [(redos, "P:never")]);

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Studio, r.Category);
        Assert.Equal("S:42", r.DestinationRootTemplate);
    }
}

/// <summary>Unorganized items route to the unorganized destination, not skipped (ROUTE-05).</summary>
public sealed class DestinationResolverUnorganizedRouteTests
{
    [Fact]
    public void UnorganizedItem_RoutesToUnorganizedDestination()
    {
        var e = new RenamerEntity(1, RenamerFileKind.Video, "T", null, null, null, Organized: false,
            [], [], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, "x")]);
        var o = new RenamerOptions { UnorganizedDestination = "U:dest" };

        var r = DestinationResolver.Resolve(e, o, new RouteLookups(
            new Dictionary<int, string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.Ordinal), []));

        Assert.Equal(RouteCategory.Unorganized, r.Category);
        Assert.Equal("U:dest", r.DestinationRootTemplate);
    }

    [Fact]
    public void UnorganizedItem_WithoutUnorganizedDestination_FallsThrough()
    {
        // No unorganized destination set → the unorganized slot does NOT fire; falls to source-confine.
        var e = new RenamerEntity(1, RenamerFileKind.Video, "T", null, null, null, Organized: false,
            [], [], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, "x")]);

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), new RouteLookups(
            new Dictionary<int, string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.Ordinal), []));

        Assert.Equal(RouteCategory.SourceConfine, r.Category);
    }
}

/// <summary>
/// The milestone hard gate (ROUTE-04 / D-05): default-relocate is OFF by default. The SAME
/// unmatched entity + options-but-for-the-flag proves the guard is the flag, not a missing feature.
/// </summary>
public sealed class DestinationResolverDefaultRelocateDisabledTests
{
    private static RenamerEntity Unmatched()
        => new(1, RenamerFileKind.Video, "T", null, null, null, true,
               [], [], [new RenamerFile(1, RenamerFileKind.Video, "a.mkv", 1, "media/in")]);

    private static RouteLookups Empty()
        => new(new Dictionary<int, string>(),
               new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
               new Dictionary<string, string>(StringComparer.Ordinal), []);

    [Fact]
    public void FlagOff_UnmatchedItem_StaysSourceConfine_NoRelocate()
    {
        var o = new RenamerOptions { DefaultDestination = "D:dest", EnableDefaultRelocate = false };

        var r = DestinationResolver.Resolve(Unmatched(), o, Empty());

        Assert.Equal(RouteCategory.SourceConfine, r.Category);
        Assert.Null(r.DestinationRootTemplate);
    }

    [Fact]
    public void FlagOn_SameUnmatchedItem_RoutesToDefault()
    {
        var o = new RenamerOptions { DefaultDestination = "D:dest", EnableDefaultRelocate = true };

        var r = DestinationResolver.Resolve(Unmatched(), o, Empty());

        Assert.Equal(RouteCategory.Default, r.Category);
        Assert.Equal("D:dest", r.DestinationRootTemplate);
    }
}

/// <summary>
/// EXCL-01/02/03 (D-03/D-04): excludes run FIRST in the resolver — a matching tag / studio (incl.
/// parent, stable id) / source-path (exact + regex) returns <see cref="RouteCategory.Excluded"/>
/// BEFORE any routing category (including Unorganized) is considered, with a clear label. A
/// match-time ReDoS timeout on an exclude regex is treated as no-match (classify-not-throw), never
/// aborting resolution. PURE — no DB, no disk.
/// </summary>
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
            Performers: [], Tags: tags ?? [],
            Files: [new RenamerFile(1, RenamerFileKind.Video, "clip.mkv", 1, parentFolderPath)],
            StudioId: studioId, ParentStudios: parentStudios);

    private static RouteLookups Lookups(
        IReadOnlyDictionary<int, string>? studios = null,
        IReadOnlyDictionary<string, string>? tags = null,
        IReadOnlyDictionary<string, string>? pathExact = null,
        IReadOnlyList<(Regex, string)>? pathRegex = null,
        IReadOnlySet<string>? excludeTags = null,
        IReadOnlySet<int>? excludeStudios = null,
        IReadOnlySet<string>? excludePathsExact = null,
        IReadOnlyList<Regex>? excludePathRegex = null)
        => new(
            studios ?? new Dictionary<int, string>(),
            tags ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            pathExact ?? new Dictionary<string, string>(StringComparer.Ordinal),
            pathRegex ?? [],
            excludeTags, excludeStudios, excludePathsExact, excludePathRegex);

    private static HashSet<string> TagSet(params string[] names)
        => new(names, StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> PathSet(params string[] paths)
        => new(paths, DestinationResolver.SourcePathComparer);

    // --- EXCL-01: tag ---------------------------------------------------------------------------

    [Fact]
    public void ExcludeByTag_Exact_ReturnsExcluded()
    {
        var e = Entity(tags: ["anime"]);
        var lk = Lookups(excludeTags: TagSet("anime"));

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
        Assert.Equal("Exclude:Tag:anime", r.MatchedRule);
        Assert.Null(r.DestinationRootTemplate);
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
        // The SAME tag is both a route and an exclude → the exclude wins (runs first).
        var e = Entity(tags: ["anime"]);
        var lk = Lookups(
            tags: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["anime"] = "T:anime" },
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
            studios: new Dictionary<int, string> { [42] = "S:42" },
            excludeStudios: new HashSet<int> { 42 });

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
    }

    [Fact]
    public void Exclude_BeatsUnorganized()
    {
        // An unorganized item that matches an exclude is Excluded, NOT routed to the unorganized dest.
        var e = Entity(organized: false, tags: ["anime"]);
        var o = new RenamerOptions { UnorganizedDestination = "U:dest" };
        var lk = Lookups(excludeTags: TagSet("anime"));

        var r = DestinationResolver.Resolve(e, o, lk);

        Assert.Equal(RouteCategory.Excluded, r.Category);
    }

    [Fact]
    public void NoExcludeMatch_FallsThroughToRoutingUnchanged()
    {
        // An entity whose tag is NOT excluded still routes normally (additive / non-breaking).
        var e = Entity(tags: ["keep"]);
        var lk = Lookups(
            tags: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["keep"] = "T:keep" },
            excludeTags: TagSet("anime"));

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.Tag, r.Category);
        Assert.Equal("T:keep", r.DestinationRootTemplate);
    }

    [Fact]
    public void NullExcludeLookups_BehaveAsEmpty_NoRegression()
    {
        // The legacy 4-arg lookups (exclude params default null) must never exclude anything.
        var e = Entity(studioId: 42, tags: ["anime"], parentFolderPath: "media/protected");
        var lk = new RouteLookups(
            new Dictionary<int, string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.Ordinal),
            []);

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.SourceConfine, r.Category);
    }

    // --- ReDoS: match-time timeout on an exclude regex = no-match (classify-not-throw) -----------

    [Fact]
    public void ExcludeRegex_Backtracking_TimesOut_TreatedAsNoMatch_NotThrow()
    {
        // Classic ReDoS pattern + a long non-matching path → catastrophic backtracking. A tiny match
        // timeout makes it fast/deterministic. The timeout must be a NO-MATCH (the item is NOT
        // excluded by that rule) and must NOT throw — so resolution completes as SourceConfine.
        var redos = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(50));
        string evil = new string('a', 40) + "!";
        var e = Entity(parentFolderPath: evil);
        var lk = Lookups(excludePathRegex: [redos]);

        var r = DestinationResolver.Resolve(e, new RenamerOptions(), lk);

        Assert.Equal(RouteCategory.SourceConfine, r.Category);
    }
}
