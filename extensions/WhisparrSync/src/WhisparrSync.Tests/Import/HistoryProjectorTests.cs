using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Import;

namespace WhisparrSync.Tests.Import;

/// <summary>
/// What one history record, and one page of them, are read as.
/// </summary>
/// <remarks>
/// The event vocabulary here is the history route's, which spells in camelCase what the webhook
/// surface spells in PascalCase. The pin below is the history spelling, and a projector that answered
/// to the webhook's would act on nothing an instance's history ever holds.
/// </remarks>
public sealed class HistoryProjectorTests
{
    private const string ImportedPath = "/whisparr-media/scene.mp4";

    /// <summary>
    /// The event type this product acts on, transcribed by hand from the history route's own
    /// rendering.
    /// </summary>
    /// <remarks>
    /// Written out rather than read off the constant it checks: an expectation computed from the
    /// module it checks agrees with that module however wrong both are.
    /// </remarks>
    [Fact]
    public void TheActedEventTypeIsTheHistorySpelling()
        => Assert.Equal("downloadFolderImported", HistoryProjector.ImportedEventType);

    [Fact]
    public void AnImportRecordProjectsToACandidateAtTheImportedPath()
    {
        var reading = HistoryProjector.Read(WhisparrGeneration.V3, Record(ImportedPath));

        Assert.Equal(HistoryProjectionOutcome.Projected, reading.Outcome);
        Assert.Equal(ImportedPath, reading.Candidate?.ReportedPath);
        Assert.Equal(WhisparrGeneration.V3, reading.Candidate?.Generation);
        Assert.Equal(HistoryProjector.ImportedEventType, reading.Candidate?.EventType);
    }

    /// <summary>
    /// A history record yields no size.
    /// </summary>
    /// <remarks>
    /// A size has not been shown to live on one, so none is read, and a candidate with no size is
    /// verified on presence alone.
    /// </remarks>
    [Fact]
    public void AProjectedCandidateCarriesNoSize()
        => Assert.Null(HistoryProjector.Read(WhisparrGeneration.V3, Record(ImportedPath)).Candidate?.ReportedSize);

    /// <summary>
    /// Each lineage's record yields the identifier its own metadata entity declares.
    /// </summary>
    /// <remarks>
    /// The entity and the member are the ones the live channel reads for the same lineage, which is
    /// what makes one scene the same scene whichever channel reported it. Transcribed by hand from
    /// an instance's own answer on each lineage.
    /// </remarks>
    [Theory]
    [InlineData("v3")]
    [InlineData("v2")]
    public void ARecordProjectsTheIdentifierItsOwnLineagesEntityDeclares(string lineage)
    {
        var (generation, record, declared) = Identified(lineage);

        var reading = HistoryProjector.Read(generation, record);

        Assert.Equal(HistoryProjectionOutcome.Projected, reading.Outcome);
        Assert.Equal(declared, reading.Candidate?.RemoteId);
    }

    /// <summary>
    /// A record read as the other lineage yields no identifier.
    /// </summary>
    /// <remarks>
    /// The discriminating control for the two cases above: a reader that looked in both places would
    /// pass them with the per-lineage rule untested.
    /// </remarks>
    [Theory]
    [InlineData("v3")]
    [InlineData("v2")]
    public void ARecordReadAsTheOtherLineageYieldsNoIdentifier(string lineage)
    {
        var (generation, record, _) = Identified(lineage);
        var other = generation == WhisparrGeneration.V3 ? WhisparrGeneration.V2 : WhisparrGeneration.V3;

        Assert.Null(HistoryProjector.Read(other, record).Candidate?.RemoteId);
    }

    /// <summary>
    /// A record whose answer embedded no entity still projects, carrying no identifier.
    /// </summary>
    /// <remarks>
    /// The residual. A file an instance never matched is still a file to register, and the absence is
    /// what tells a later match there is nothing to match on.
    /// </remarks>
    [Fact]
    public void ARecordCarryingNoEntityStillProjectsWithNoIdentifier()
    {
        var reading = HistoryProjector.Read(WhisparrGeneration.V3, Record(ImportedPath));

        Assert.Equal(HistoryProjectionOutcome.Projected, reading.Outcome);
        Assert.Null(reading.Candidate?.RemoteId);
    }

    /// <summary>
    /// Each lineage's own unset rendering yields no identifier.
    /// </summary>
    /// <remarks>
    /// One lineage omits its member or leaves it blank; the other carries a number its schema starts
    /// every row at. Read as an identifier, an unset rendering would make every unmatched scene the
    /// same scene.
    /// </remarks>
    [Theory]
    [InlineData("v3")]
    [InlineData("v2")]
    public void AnUnsetIdentifierYieldsNone(string lineage)
    {
        var (generation, record, _) = Unidentified(lineage);

        Assert.Null(HistoryProjector.Read(generation, record).Candidate?.RemoteId);
    }

    [Theory]
    [InlineData("grabbed")]
    [InlineData("downloadFailed")]
    [InlineData("movieFileDeleted")]
    [InlineData("Download")]
    public void AnEventTypeThisProductDoesNotActOnProjectsToNothing(string eventType)
    {
        var record = Record(ImportedPath);
        record["eventType"] = eventType;

        var reading = HistoryProjector.Read(WhisparrGeneration.V3, record);

        Assert.Equal(HistoryProjectionOutcome.Ignored, reading.Outcome);
        Assert.Null(reading.Candidate);
        Assert.Equal(eventType, reading.EventType);
    }

    /// <summary>An import record with no path is named rather than silently dropped.</summary>
    [Fact]
    public void AnImportRecordCarryingNoPathIsNamed()
    {
        var record = Record(ImportedPath);
        record["data"] = new JsonObject();

        var reading = HistoryProjector.Read(WhisparrGeneration.V3, record);

        Assert.Equal(HistoryProjectionOutcome.NoReadablePath, reading.Outcome);
        Assert.Null(reading.Candidate);
    }

    [Fact]
    public void ARecordNamingNoEventTypeIsIgnored()
        => Assert.Equal(
            HistoryProjectionOutcome.Ignored,
            HistoryProjector.Read(WhisparrGeneration.V3, new JsonObject()).Outcome);

    [Fact]
    public void AbsentRecordsAreIgnored()
        => Assert.Equal(
            HistoryProjectionOutcome.Ignored,
            HistoryProjector.Read(WhisparrGeneration.V3, null).Outcome);

    [Fact]
    public void ThePagedEnvelopesRecordsAreRead()
    {
        var records = HistoryProjector.RecordsIn(
            $$"""{"page":1,"pageSize":50,"totalRecords":2,"records":[{{Record(ImportedPath).ToJsonString()}},{{Record(ImportedPath).ToJsonString()}}]}""");

        Assert.Equal(2, records?.Count);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"records":{}}""")]
    public void AnAnswerThatIsNotAPageReadsAsNoPage(string body)
        => Assert.Null(HistoryProjector.RecordsIn(body));

    /// <summary>
    /// An empty page is read as an empty page rather than as an unreadable answer.
    /// </summary>
    /// <remarks>
    /// The discriminating control for the four above: an answer nobody could read and a page holding
    /// nothing mean opposite things to a walk, and one refuses the pass while the other ends it.
    /// </remarks>
    [Fact]
    public void AnEmptyPageIsAPage()
        => Assert.Empty(Assert.IsType<JsonArray>(HistoryProjector.RecordsIn("""{"records":[]}""")));

    [Fact]
    public void EachRecordsInstantIsReadInPageOrder()
    {
        var page = new JsonArray(
            Record(ImportedPath, "2026-08-30T12:00:00Z"),
            Record(ImportedPath, "2026-08-30T11:59:00Z"));

        Assert.Equal(
            [
                new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 30, 11, 59, 0, TimeSpan.Zero),
            ],
            HistoryProjector.InstantsIn(page));
    }

    /// <summary>
    /// An instant rendered without a zone is read as one, rather than as the reader's own.
    /// </summary>
    /// <remarks>
    /// The stop rule compares this against a stored instant, so a page read in the host container's
    /// zone would place every record by however that container is configured.
    /// </remarks>
    [Fact]
    public void AnInstantCarryingNoZoneIsReadAsUniversal()
        => Assert.Equal(
            new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
            Assert.IsType<DateTimeOffset>(
                HistoryProjector.InstantsIn(new JsonArray(Record(ImportedPath, "2026-08-30T12:00:00")))?[0]));

    /// <summary>A page carrying a record with no readable instant yields none at all.</summary>
    [Fact]
    public void APageCarryingARecordWithNoReadableInstantYieldsNone()
    {
        var undated = Record(ImportedPath);
        undated.Remove("date");

        Assert.Null(HistoryProjector.InstantsIn(new JsonArray(Record(ImportedPath), undated)));
    }

    private static JsonObject Record(string path, string date = "2026-08-30T12:00:00Z")
        => new()
        {
            ["eventType"] = HistoryProjector.ImportedEventType,
            ["date"] = date,
            ["sourceTitle"] = "Cove.E2E.Seeded.0.1080p.WEB-DL",
            ["data"] = new JsonObject { ["importedPath"] = path },
        };

    /// <summary>
    /// One lineage, one record its own instance would answer with, and the identifier it declares.
    /// </summary>
    /// <remarks>
    /// The entity member, the identifier member and the JSON type of each are transcribed by hand
    /// from a live answer on that lineage.
    /// </remarks>
    private static (WhisparrGeneration Generation, JsonObject Record, string Declared) Identified(
        string lineage)
        => lineage switch
        {
            "v3" => (
                WhisparrGeneration.V3,
                Embedding("movie", new JsonObject { ["stashId"] = "1703a150-ceec-4953-ac10-d7ebc7d0974f" }),
                "1703a150-ceec-4953-ac10-d7ebc7d0974f"),
            "v2" => (
                WhisparrGeneration.V2,
                Embedding("episode", new JsonObject { ["tvdbId"] = 4149372 }),
                "4149372"),
            _ => throw new ArgumentOutOfRangeException(nameof(lineage)),
        };

    /// <summary>One lineage, and a record whose entity was never matched to a scene.</summary>
    private static (WhisparrGeneration Generation, JsonObject Record, string Declared) Unidentified(
        string lineage)
        => lineage switch
        {
            "v3" => (
                WhisparrGeneration.V3,
                Embedding("movie", new JsonObject { ["stashId"] = "" }),
                ""),
            "v2" => (
                WhisparrGeneration.V2,
                Embedding("episode", new JsonObject { ["tvdbId"] = 0 }),
                "0"),
            _ => throw new ArgumentOutOfRangeException(nameof(lineage)),
        };

    private static JsonObject Embedding(string member, JsonObject entity)
    {
        var record = Record(ImportedPath);
        record[member] = entity;
        return record;
    }
}
