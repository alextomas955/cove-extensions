using System.Text.Json.Nodes;
using Renamer.Options;

namespace Renamer.Tests.Options;

public sealed class OptionsMigrationScanTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    public void UnusableBlob_NeedsNothing(string? json)
    {
        var scan = OptionsMigration.Scan(json);

        Assert.False(scan.Any);
        Assert.Empty(scan.Tags);
        Assert.Empty(scan.Performers);
    }

    [Fact]
    public void EmptyLegacyCollections_NeedNothing()
    {
        // The pre-conversion panel serialized its whole defaults object, so a user who configured no
        // rule at all still stored these keys empty. Treating a present-but-empty key as work would
        // defer the conversion forever on such an install.
        const string json = """
            {
              "Tags": { "Whitelist": [], "Blacklist": [] },
              "Performers": { "Whitelist": [], "Blacklist": [] },
              "ExcludeTags": [],
              "TagDestinations": {}
            }
            """;

        Assert.False(OptionsMigration.Scan(json).Any);
    }

    [Fact]
    public void EveryLegacySite_ContributesItsNames()
    {
        const string json = """
            {
              "Tags": { "Whitelist": ["keepTag"], "Blacklist": ["dropTag"] },
              "Performers": { "Whitelist": ["keepP"], "Blacklist": ["dropP"] },
              "ExcludeTags": ["excludedTag"],
              "TagDestinations": { "routedTag": "/dest" }
            }
            """;

        var scan = OptionsMigration.Scan(json);

        Assert.Equal(["dropTag", "excludedTag", "keepTag", "routedTag"], scan.Tags.Order(StringComparer.Ordinal));
        Assert.Equal(["dropP", "keepP"], scan.Performers.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RepeatedName_IsAskedForOnce()
    {
        const string json = """
            {
              "Tags": { "Whitelist": ["anime"], "Blacklist": ["ANIME"] },
              "ExcludeTags": ["Anime"],
              "TagDestinations": { "aNiMe": "/dest" }
            }
            """;

        Assert.Single(OptionsMigration.Scan(json).Tags);
    }

    [Fact]
    public void AlreadyConvertedBlob_NeedsNothing()
    {
        const string json = """
            {
              "Tags": { "WhitelistIds": [1], "BlacklistIds": [2] },
              "Performers": { "WhitelistIds": [3] },
              "ExcludeTagIds": [4],
              "TagDestinations": { "5": "/dest" }
            }
            """;

        Assert.False(OptionsMigration.Scan(json).Any);
    }

    [Fact]
    public void LegacyKeyInAnotherCasing_IsStillFound()
    {
        // RenamerOptions.JsonOptions binds case-insensitively, so a blob spelled this way is one the
        // model accepts. Missing it here would stamp it as converted and leave its rules unbindable.
        const string json = """
            {
              "tags": { "whitelist": ["anime"] },
              "excludeTags": ["raw"],
              "tagDestinations": { "drama": "/d" }
            }
            """;

        var scan = OptionsMigration.Scan(json);

        Assert.Equal(["anime", "drama", "raw"], scan.Tags.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("9", false)]
    [InlineData("0", false)]
    [InlineData("2147483647", false)]
    [InlineData("-9", false)]
    [InlineData("+9", false)]
    [InlineData("09", false)]
    [InlineData(" 9 ", false)]
    [InlineData("\t9\n", false)]
    [InlineData("1e3", true)]
    [InlineData("1.5", true)]
    [InlineData("9,9", true)]
    [InlineData("2147483648", true)]
    [InlineData("-2147483649", true)]
    [InlineData("\u00A09", true)]
    [InlineData("", true)]
    [InlineData("Anime", true)]
    public void DestinationKeySpelling_DecidesNameOrId(string key, bool isName)
    {
        // The settings endpoint reports this scan's answer as the pending conversion, and refuses a
        // save while it reports work, so a spelling read the wrong way either blocks every save or lets
        // one write over rules still stored by name.
        var root = new JsonObject
        {
            ["TagDestinations"] = new JsonObject { [key] = "D:/x" },
        };

        var scan = OptionsMigration.Scan(root.ToJsonString());

        Assert.Equal(isName, scan.Any);
        if (isName)
        {
            Assert.Equal([key], scan.Tags);
        }
        else
        {
            Assert.Empty(scan.Tags);
        }
    }
}
