using System.Text.Json.Nodes;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

// A link in an entity's folder is named after the identity of the file it points at, so an instance
// parsing a studio and a date out of a file name reads nothing out of one. These cover what each
// generation composes when the answer is supplied instead.
public class SuppliedEntryTests
{
    private static JsonObject Entry()
        => new()
        {
            ["path"] = "/data/.wsync-v2/325632/deadbeef-1234.mp4",
            ["quality"] = new JsonObject { ["quality"] = new JsonObject { ["id"] = 7 } },
            ["languages"] = new JsonArray(new JsonObject { ["id"] = 1 }),
        };

    [Fact]
    public void ASiteGenerationNamesBothTheSiteRowAndTheSceneRow()
    {
        var composed = V2PayloadReader.Reading.IdentifiedEntry(Entry(), new EntryAddress(88, 12));

        Assert.NotNull(composed);
        Assert.Equal(12, composed["seriesId"]!.GetValue<int>());
        Assert.Equal([88], composed["episodeIds"]!.AsArray().Select(id => id!.GetValue<int>()));
    }

    // Without the site there is no catalogue to name an episode inside, and an entry naming an
    // episode row under no site is accepted and records nothing.
    [Fact]
    public void ASiteGenerationRefusesAnAddressNamingNoSite()
        => Assert.Null(V2PayloadReader.Reading.IdentifiedEntry(Entry(), new EntryAddress(88)));

    [Fact]
    public void ASiteGenerationRefusesAnAddressWhoseSiteRowIsNotOne()
        => Assert.Null(V2PayloadReader.Reading.IdentifiedEntry(Entry(), new EntryAddress(88, 0)));

    // This generation holds a scene as a row of its own, so the site half is not read and its
    // absence is not a refusal.
    [Fact]
    public void ASceneGenerationNamesTheSceneRowAlone()
    {
        var composed = V3PayloadReader.Reading.IdentifiedEntry(Entry(), new EntryAddress(88));

        Assert.NotNull(composed);
        Assert.Equal(88, composed["movieId"]!.GetValue<int>());
        Assert.Null(composed["seriesId"]);
    }
}
