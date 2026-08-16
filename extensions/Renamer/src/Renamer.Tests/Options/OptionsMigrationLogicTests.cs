using System.Text.Json;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Options;

/// <summary>
/// The pure half of the one-time stored-options conversions: name→id resolution with its case-collapse
/// narrowing and dropped-name trail, the destination rewrite onto Cove's library paths, and the
/// preservation of everything neither converter models. No store, no database context — every input
/// here is hand-written, so nothing agrees with the code under test by construction.
/// </summary>
[Trait("Tier", "L0")]
public sealed class OptionsMigrationLogicTests
{
    private static readonly (int Id, string Name)[] Tags =
    [
        (9, "Anime"),
        (15, "Sample"),
        (88, "4K"),
        (104, "Trailer"),
    ];

    private static readonly (int Id, string Name)[] Performers = [(311, "Jane Doe"), (312, "John Roe")];

    /// <summary>
    /// The blob a user who installed before the identity migration actually has on disk, transcribed by
    /// hand from the shape the shipped panel wrote — never generated from the code under test, which
    /// would only prove the converter agrees with itself.
    /// </summary>
    private const string LegacyBlob =
        """
        {
          "FilenameTemplate": "{$date - }$title",
          "FolderTemplate": "$studio",
          "AllowedRoots": ["/media"],
          "RequiredFields": ["title"],
          "Performers": { "Separator": " ", "Whitelist": ["jane doe"], "Blacklist": [] },
          "Tags": { "Separator": " ", "Whitelist": ["4k"], "Blacklist": ["Trailer"] },
          "StudioDestinations": { "42": "/media/studio-a" },
          "TagDestinations": { "Anime": "/media/anime" },
          "ExcludeTags": ["Sample", "4K"],
          "ExcludeStudioIds": [7],
          "AnUnmodelledKeyFromSomeFutureVersion": { "keep": "me" }
        }
        """;

    private static RenamerOptions Reload(string json) =>
        JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions)!;

    [Fact]
    public void ALegacyBlob_Converts_WithoutLosingAnyFieldTheConverterDoesNotModel()
    {
        // Both halves, in the order the initialize seam runs them: the id-keying leaves destination
        // values as the strings they were stored as, and the destination half is what makes the blob
        // loadable into the current model at all.
        var converted = OptionsMigration.Convert(LegacyBlob, Tags, Performers);
        var placed = OptionsMigration.ConvertDestinationsToRoots(converted.Json, ["/media"]);
        var options = Reload(placed.Json);

        // The six migrated fields, resolved.
        Assert.Equal([88], options.Tags.WhitelistIds);
        Assert.Equal([104], options.Tags.BlacklistIds);
        Assert.Equal([311], options.Performers.WhitelistIds);
        Assert.Empty(options.Performers.BlacklistIds);
        Assert.Equal([15, 88], options.ExcludeTagIds);
        // The destination the rule used to produce, spelled the new way: the library path holding the
        // stored root, plus the rest of that root followed by the old global folder template.
        Assert.Equal(
            new Dictionary<int, Destination> { [9] = Dests.At("/media", "anime/$studio") },
            options.TagDestinations);

        // Everything else, untouched — including the fields a failed load would have reset to defaults.
        Assert.Equal("{$date - }$title", options.FilenameTemplate);
        Assert.Equal("$studio", options.FolderTemplate);
        Assert.Equal(["/media"], options.AllowedRoots);
        Assert.Equal(["title"], options.RequiredFields);
        Assert.Equal(" ", options.Tags.Separator);
        Assert.Equal(
            new Dictionary<int, Destination> { [42] = Dests.At("/media", "studio-a/$studio") },
            options.StudioDestinations);
        Assert.Equal([7], options.ExcludeStudioIds);

        // A key the converter does not model at all still survives the rewrite.
        using var raw = JsonDocument.Parse(placed.Json);
        Assert.Equal(
            "me",
            raw.RootElement.GetProperty("AnUnmodelledKeyFromSomeFutureVersion").GetProperty("keep").GetString());

        // No legacy spelling is left behind for a later reader to half-honour.
        Assert.False(raw.RootElement.TryGetProperty("ExcludeTags", out _));
        Assert.False(raw.RootElement.GetProperty("Tags").TryGetProperty("Whitelist", out _));
        Assert.False(raw.RootElement.GetProperty("Performers").TryGetProperty("Whitelist", out _));
    }

    // ── Resolution ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("4K", 88)]
    [InlineData("4k", 88)]
    [InlineData("4k ", null)]   // trimming was never part of matching; only letter case was
    [InlineData("aNiMe", 9)]
    public void AStoredNameResolves_RegardlessOfLetterCase(string stored, int? expected)
    {
        // Matching a rule against a live entity name was OrdinalIgnoreCase before this migration, so a
        // rule stored in a different case was LIVE and a case-sensitive lookup would silently drop it.
        var converted = OptionsMigration.Convert(
            $$"""{ "ExcludeTags": ["{{stored}}"] }""", Tags, Performers);

        Assert.Equal(expected is null ? [] : (int[])[expected.Value], Reload(converted.Json).ExcludeTagIds);
    }

    [Fact]
    public void ARuleThatCoveredCaseVariantRows_ReportsWhatItNoLongerCovers_OnceAndByData()
    {
        // The failure this reports: the blacklist entry suppressed ALL THREE 4K rows before the
        // migration and suppresses only 70 after — so every file tagged 4k, or tagged by the second
        // spelling, starts rendering that tag into its filename. The name resolved, so the dropped-name
        // trail says nothing; without this the change is invisible.
        //
        // "Dupe"/"dupe" is a case-variant pair NO stored rule names. Reporting every such pair in a
        // library would bury the ones a rule actually narrows, so it must stay out of the report.
        (int, string)[] rows = [(70, "4K"), (71, "4k"), (72, "4K"), (80, "Dupe"), (81, "dupe")];
        const string blob =
            """{ "Tags": { "Blacklist": ["4K"] }, "ExcludeTags": ["4k"], "TagDestinations": { "4K": "/x" } }""";

        var converted = OptionsMigration.Convert(blob, rows, Performers);

        // Named across three fields, and reported ONCE — with the third row not lost from the trail.
        var collapse = Assert.Single(converted.CaseCollapses);
        Assert.Equal("4K", collapse.Name);
        Assert.Equal(70, collapse.MatchedId);
        Assert.Equal([71, 72], collapse.AlsoMatchedIds);
        Assert.Empty(converted.DroppedNames);

        // Which row the rule now matches is decided by the DATA — the lowest id — and not by the order
        // the rows came back in, which is not something the user chose.
        (int, string)[] descending = [(81, "dupe"), (80, "Dupe"), (72, "4K"), (71, "4k"), (70, "4K")];

        var reversed = Assert.Single(OptionsMigration.Convert(blob, descending, Performers).CaseCollapses);
        Assert.Equal(70, reversed.MatchedId);
        Assert.Equal([71, 72], reversed.AlsoMatchedIds);
    }

    [Fact]
    public void AStoredNameMatchingNothing_IsDropped_AndReported()
    {
        // The same value the host-absent styles spec seeds for its stale-chip fixture, so the two agree.
        var converted = OptionsMigration.Convert(
            """{ "ExcludeTags": ["tag-that-no-longer-exists", "Sample"] }""", Tags, Performers);

        Assert.Equal([15], Reload(converted.Json).ExcludeTagIds);
        Assert.Equal(["tag-that-no-longer-exists"], converted.DroppedNames);
    }

    [Fact]
    public void EveryDroppedName_AcrossAllSixFields_IsReported()
    {
        const string blob =
            """
            {
              "Performers": { "Whitelist": ["Nobody"], "Blacklist": ["Nobody Else"] },
              "Tags": { "Whitelist": ["ghost-a"], "Blacklist": ["ghost-b"] },
              "TagDestinations": { "ghost-c": "/media/x" },
              "ExcludeTags": ["ghost-d"]
            }
            """;

        var converted = OptionsMigration.Convert(blob, Tags, Performers);

        Assert.Equal(
            ["ghost-a", "ghost-b", "Nobody", "Nobody Else", "ghost-d", "ghost-c"],
            converted.DroppedNames);
        var options = Reload(converted.Json);
        Assert.Empty(options.Tags.WhitelistIds);
        Assert.Empty(options.Tags.BlacklistIds);
        Assert.Empty(options.Performers.WhitelistIds);
        Assert.Empty(options.Performers.BlacklistIds);
        Assert.Empty(options.ExcludeTagIds);
        Assert.Empty(options.TagDestinations);
    }

    /// <summary>
    /// Goes RED the moment any of the six migrated fields is name-keyed again. A future change that
    /// reintroduces a name-valued whitelist, exclude list or destination key fails HERE, on a
    /// serialize/deserialize round-trip of a converted blob, rather than at run time as a rule that
    /// quietly stops matching.
    /// </summary>
    [Fact]
    public void EverySixthMigratedField_SurvivesARoundTripAsIdValued_ReintroducingNameKeyingFailsHere()
    {
        // Both halves, because this asserts the shape of a blob that is FULLY converted: the id-keying
        // leaves destination values as stored strings, which the current model cannot load at all, so a
        // reload of that intermediate would be asserting about a state no reader ever sees.
        var placed = OptionsMigration.ConvertDestinationsToRoots(
            OptionsMigration.Convert(LegacyBlob, Tags, Performers).Json, ["/media"]);
        var converted = Reload(placed.Json);
        var json = JsonSerializer.Serialize(converted, RenamerOptions.JsonOptions);
        var reloaded = Reload(json);

        Assert.Equal(converted, reloaded);

        using var raw = JsonDocument.Parse(json);
        AssertIdArray(raw.RootElement.GetProperty("Tags"), "WhitelistIds");
        AssertIdArray(raw.RootElement.GetProperty("Tags"), "BlacklistIds");
        AssertIdArray(raw.RootElement.GetProperty("Performers"), "WhitelistIds");
        AssertIdArray(raw.RootElement.GetProperty("Performers"), "BlacklistIds");
        AssertIdArray(raw.RootElement, "ExcludeTagIds");

        foreach (var entry in raw.RootElement.GetProperty("TagDestinations").EnumerateObject())
        {
            Assert.True(
                int.TryParse(entry.Name, out _),
                $"TagDestinations key '{entry.Name}' is not an id — a name-keyed tag rule is back.");
        }

        static void AssertIdArray(JsonElement owner, string property)
        {
            foreach (var item in owner.GetProperty(property).EnumerateArray())
            {
                Assert.True(
                    item.ValueKind == JsonValueKind.Number,
                    $"{property} holds {item.ValueKind} — a name-valued rule list is back.");
            }
        }
    }

    // ── Destination keys: the one ambiguous field ─────────────────────────────

    [Fact]
    public void TwoDestinationNamesResolvingToOneId_KeepTheFirstInDocumentOrder_AndTheLoserIsReported()
    {
        // Also the crash case: the panel's string-keyed map editor treats "4K" and "4k" as two rules, so
        // this blob is reachable. Reading it through a case-INSENSITIVE JsonNode parse throws
        // ArgumentException the first time the map is enumerated, which would abort the whole
        // conversion and leave the settings permanently unreadable.
        //
        // Which of the two survives is decided by JSON document order, which is not something the user
        // chose — so the one that loses is named rather than vanishing.
        var converted = OptionsMigration.Convert(
            """{ "TagDestinations": { "4K": "/media/first", "4k": "/media/second" } }""", Tags, Performers);

        Assert.Equal(
            new Dictionary<int, Destination> { [88] = Dests.At("/media", "first") },
            Reload(OptionsMigration.ConvertDestinationsToRoots(converted.Json, ["/media"]).Json)
                .TagDestinations);
        Assert.Equal(
            [new OptionsMigration.DiscardedDestination("4k", 88, "4K")],
            converted.DiscardedDestinations);
        Assert.Empty(converted.DroppedNames);
    }

    // ── The destination conversion: a typed root becomes a library path + a relative template ─────
    //
    // The three cases are one boundary. A conversion that placed everything, one that dropped
    // everything, and one that converted against an empty list would each satisfy any two of them.

    /// <summary>A stored blob shaped like the one a real install has: rules under a library path, and one that is not.</summary>
    private const string TypedRootsBlob =
        """
        {
          "FolderTemplate": "$studio",
          "StudioDestinations": { "101": "I:\\Downloads\\P\\videos" },
          "PathDestinations": [
            { "Pattern": "G:/in", "Dest": "G:/Downloads/P/videos", "IsRegex": false },
            { "Pattern": "G:/junk", "Dest": "E:/archive/off-library", "IsRegex": false }
          ],
          "UnorganizedDestination": "G:\\Downloads\\P\\unsorted"
        }
        """;

    private static readonly string[] LibraryPaths = [@"G:\Downloads\P", @"I:\Downloads\P"];

    [Fact]
    public void AStoredRoot_BecomesTheLibraryPathHoldingIt_PlusTheRestAndTheOldFolderTemplate()
    {
        // Behaviour-preserving, and the arithmetic is the whole claim: the rule used to land an item at
        // its stored root with the global folder template rendered underneath, so the same folder is
        // (the library path holding that root) + (the rest of it)/(that same template).
        var converted = OptionsMigration.ConvertDestinationsToRoots(TypedRootsBlob, LibraryPaths);
        var options = Reload(converted.Json);

        Assert.False(converted.Deferred);
        Assert.Equal(
            Dests.At("I:/Downloads/P", "videos/$studio"),
            options.StudioDestinations[101]);
        Assert.Equal(
            Dests.At("G:/Downloads/P", "unsorted/$studio"),
            options.UnorganizedDestination);

        // The default is left exactly as stored: its own root defaults to "the file's own library
        // path", which is what a relative template has always been measured from, so there is nothing
        // to convert and nothing to guess.
        Assert.Equal("$studio", options.FolderTemplate);
        Assert.Equal(string.Empty, options.FolderRoot);
    }

    [Fact]
    public void AStoredRootUnderNoLibraryPath_IsDropped_AndNamedInTheTrail()
    {
        // Owner decision: there is no root to CHOOSE for such a rule, and inventing one would relocate
        // files. Its items follow the default afterwards — a behaviour change, which is why the trail
        // names it rather than the rule merely disappearing.
        var converted = OptionsMigration.ConvertDestinationsToRoots(TypedRootsBlob, LibraryPaths);
        var options = Reload(converted.Json);

        Assert.Equal(
            [new OptionsMigration.DroppedDestination("PathDestinations[1]", "E:/archive/off-library")],
            converted.Dropped);

        // The SURVIVING rule is asserted beside it: a conversion that dropped every path rule would
        // satisfy the line above on its own.
        var kept = Assert.Single(options.PathDestinations);
        Assert.Equal("G:/in", kept.Pattern);
        Assert.Equal(Dests.At("G:/Downloads/P", "videos/$studio"), kept.Dest);
    }

    [Fact]
    public void WithNoLibraryPathsAtAll_ItConvertsNothing_AndSaysSo()
    {
        // The safety argument, and it is the same one the name→id half makes about an empty library
        // read: an empty list is indistinguishable from a host that has not supplied one yet, and
        // converting against it would drop EVERY rule the user has.
        var converted = OptionsMigration.ConvertDestinationsToRoots(TypedRootsBlob, []);

        Assert.True(converted.Deferred);
        Assert.False(converted.Changed);
        Assert.Equal(TypedRootsBlob, converted.Json);
    }

    // ── Scan: what the initialize seam asks before it touches a database ──────

    [Theory]
    [InlineData(null, 0, 0)]
    [InlineData("", 0, 0)]
    [InlineData("not json at all", 0, 0)]
    [InlineData("""{ "FilenameTemplate": "$title" }""", 0, 0)]
    [InlineData("""{ "TagDestinations": { "9": "/media/anime" } }""", 0, 0)]
    [InlineData("""{ "Tags": { "WhitelistIds": [88] } }""", 0, 0)]
    [InlineData("""{ "excludetags": ["4k"] }""", 1, 0)]
    [InlineData("""{ "TagDestinations": { "Anime": "/media/anime" } }""", 1, 0)]
    [InlineData("""{ "Tags": { "Whitelist": ["4k"], "Blacklist": ["Trailer"] } }""", 2, 0)]
    [InlineData("""{ "Performers": { "Blacklist": ["Jane Doe"] } }""", 0, 1)]
    // The shape every pre-migration install actually stored: each legacy key present, none of them
    // holding a name. Counting the KEYS here instead would demand a performer table this user may
    // legitimately not have, and the conversion would defer on every start forever.
    [InlineData(
        """
        {
          "Performers": { "Whitelist": [], "Blacklist": [] },
          "Tags": { "Whitelist": [], "Blacklist": [] },
          "ExcludeTags": []
        }
        """, 0, 0)]
    [InlineData(
        """
        {
          "Performers": { "Whitelist": [], "Blacklist": [] },
          "Tags": { "Whitelist": [], "Blacklist": [] },
          "ExcludeTags": [],
          "TagDestinations": { "Anime": "/media/anime" }
        }
        """, 1, 0)]
    public void Scan_CountsTheNamesEachHalfStillNeedsResolved(string? json, int tags, int performers)
    {
        var legacy = OptionsMigration.Scan(json);

        Assert.Equal(tags, legacy.Tags);
        Assert.Equal(performers, legacy.Performers);
        Assert.Equal(tags + performers > 0, legacy.Any);
    }

    [Fact]
    public void AConvertedBlob_NeedsNoFurtherConversion()
    {
        var converted = OptionsMigration.Convert(LegacyBlob, Tags, Performers);

        Assert.False(OptionsMigration.Scan(converted.Json).Any);
    }
}
