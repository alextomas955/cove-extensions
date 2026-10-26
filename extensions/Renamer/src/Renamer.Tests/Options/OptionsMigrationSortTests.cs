using System.Text.Json;
using System.Text.Json.Nodes;
using Renamer.Options;

namespace Renamer.Tests.Options;

public sealed class OptionsMigrationSortTests
{
    // A blob the pre-conversion settings panel could have saved: a performer sort that no longer
    // names a declared member, beside settings the user chose.
    private const string PerformerBlob = """
        {"FilenameTemplate":"$studio/$title","FilenameMax":120,"Case":"Title",
         "ExcludeTagIds":[13],
         "Performers":{"Separator":" & ","MaxCount":3,"Sort":"FavoriteFirst","GenderOrder":["Female"]}}
        """;

    [Fact]
    public void APerformerSortNamingTheDroppedMember_BecomesNameAsc_AndEveryOtherStoredKeyIsKept()
    {
        var result = OptionsMigration.ConvertDroppedSorts(PerformerBlob);

        Assert.Equal(["Performers"], result.Groups);

        var before = JsonNode.Parse(PerformerBlob)!.AsObject();
        var after = JsonNode.Parse(result.Json)!.AsObject();
        Assert.Equal("NameAsc", (string?)after["Performers"]!["Sort"]);

        Assert.Equal(
            before.Select(e => e.Key).Order(StringComparer.Ordinal),
            after.Select(e => e.Key).Order(StringComparer.Ordinal));
        foreach (var entry in before.Where(e => e.Key != "Performers"))
        {
            Assert.Equal(entry.Value!.ToJsonString(), after[entry.Key]!.ToJsonString());
        }

        Assert.Equal(" & ", (string?)after["Performers"]!["Separator"]);
        Assert.Equal(3, (int?)after["Performers"]!["MaxCount"]);
        Assert.Equal("[\"Female\"]", after["Performers"]!["GenderOrder"]!.ToJsonString());
    }

    [Fact]
    public void ATagSortNamingTheDroppedMember_ConvertsToo()
    {
        // Tags never honoured the performer sorts, but a hand-edited or previously-saved blob can
        // carry one under Tags and it breaks the bind from there just the same.
        const string blob = """{"Tags":{"Sort":"FavoriteFirst"}}""";

        var result = OptionsMigration.ConvertDroppedSorts(blob);

        Assert.Equal(["Tags"], result.Groups);
        Assert.Equal("NameAsc", (string?)JsonNode.Parse(result.Json)!["Tags"]!["Sort"]);
    }

    [Fact]
    public void AGroupKeyAndValueDifferingInCase_ConvertToo()
    {
        // RenamerOptions.JsonOptions binds case-insensitively, so this is a blob the model accepts.
        // A case-sensitive lookup would leave it unconverted while the stamp recorded it as done.
        const string blob = """{"performers":{"sort":"favoriteFirst"}}""";

        var result = OptionsMigration.ConvertDroppedSorts(blob);

        Assert.Equal(["Performers"], result.Groups);
        Assert.Equal("NameAsc", (string?)JsonNode.Parse(result.Json)!["performers"]!["sort"]);
    }

    [Fact]
    public void ABlobWithNoDroppedSort_ComesBackUnchanged()
    {
        const string blob = """{"FilenameTemplate":"$title","Performers":{"Sort":"IdAsc"}}""";

        var result = OptionsMigration.ConvertDroppedSorts(blob);

        Assert.Empty(result.Groups);
        Assert.Equal(blob, result.Json);
    }

    [Fact]
    public void TheConvertedBlobBinds_WithTheUsersOtherSettingsIntact()
    {
        var result = OptionsMigration.ConvertDroppedSorts(PerformerBlob);

        var options = JsonSerializer.Deserialize<RenamerOptions>(result.Json, RenamerOptions.JsonOptions);

        Assert.NotNull(options);
        Assert.Equal("$studio/$title", options.FilenameTemplate);
        Assert.Equal(120, options.FilenameMax);
        Assert.Equal(CaseTransform.Title, options.Case);
        Assert.Equal([13], options.ExcludeTagIds);
        Assert.Equal(" & ", options.Performers.Separator);
        Assert.Equal(3, options.Performers.MaxCount);
        Assert.Equal(["Female"], options.Performers.GenderOrder);
        Assert.Equal(SortOrder.NameAsc, options.Performers.Sort);
    }

    [Fact]
    public void ConvertingTwice_ReportsNoGroupsTheSecondTime()
    {
        var once = OptionsMigration.ConvertDroppedSorts(PerformerBlob);

        var twice = OptionsMigration.ConvertDroppedSorts(once.Json);

        Assert.Empty(twice.Groups);
        Assert.Equal(once.Json, twice.Json);
    }
}
