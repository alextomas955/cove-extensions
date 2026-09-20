using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Import;

// Every instance under test has a past. Against an empty one the assertions below would all hold
// with the walk deleted.
public sealed class BackstopPassTests
{
    private const string Address = "http://whisparr:6969";
    private const string MovedAddress = "http://whisparr-elsewhere:6969";
    private const string ApiKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";
    private const string ImportedPath = "/whisparr-media/scene.mp4";

    // Digits only, so either lineage's spelling can carry it.
    private const string SceneIdentifier = "4149372";

    private const int NewestRecordId = 1000;

    private static readonly DateTimeOffset Noon = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    // The instance holds records the pass could have imported, so the mark advancing is what tells
    // "imported nothing" from "read nothing".
    [Fact]
    public async Task TheFirstPassRecordsWhereHistoryEndsAndImportsNothing()
    {
        var pass = new Pass(mark: null);
        pass.Answering(Page(Descending(3)));

        var result = await pass.RunAsync();

        Assert.Equal(BackstopPassOutcome.FirstConnect, result.Outcome);
        Assert.Empty(pass.Core.Ingested);
        Assert.Equal(Noon, (await pass.StoredAsync()).V3?.BackstopWatermarkUtc);
        Assert.Equal(1, result.PagesRead);
        Assert.Equal(0, result.RecordsTaken);
    }

    [Fact]
    public async Task TheFirstPassRecordsThePositionAsLost()
    {
        var pass = new Pass(mark: null);
        pass.Answering(Page(Descending(3)));

        await pass.RunAsync();

        Assert.True((await pass.StoredAsync()).ImportHealth.BackstopPositionLost);
    }

    // Without a mark written here, every later pass would be another first connect and the backstop
    // would never import anything.
    [Fact]
    public async Task AFirstPassOverAnEmptyHistoryStillWritesAMark()
    {
        var pass = new Pass(mark: null);
        pass.Answering(Page([]));

        await pass.RunAsync();

        Assert.Equal(Pass.Now, (await pass.StoredAsync()).V3?.BackstopWatermarkUtc);
    }

    // Five pages exist and the mark sits inside the third. A walk that read to the end would ask for
    // all five, and the records past the mark are already imported.
    [Fact]
    public async Task TheWalkStopsAtThePageHoldingTheMark()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-125));
        pass.Answering(FullPage(0), FullPage(1), FullPage(2), FullPage(3), FullPage(4));

        var result = await pass.RunAsync();

        Assert.Equal([1, 2, 3], pass.Client.Histories.Select(call => call.Page));
        Assert.Equal(3, result.PagesRead);
        Assert.Equal((2 * BackstopPass.PageSize) + 26, result.RecordsTaken);
    }

    [Fact]
    public async Task TheMarkMovesToTheNewestRecordTheWalkSaw()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-2));
        pass.Answering(Page(Descending(3)));

        var result = await pass.RunAsync();

        Assert.Equal(Noon, result.Watermark);
        Assert.Equal(Noon, (await pass.StoredAsync()).V3?.BackstopWatermarkUtc);
    }

    [Fact]
    public async Task APassOverAnEmptyHistoryLeavesTheMarkWhereItWas()
    {
        var mark = Noon.AddMinutes(-30);
        var pass = new Pass(mark);
        pass.Answering(Page([]));

        await pass.RunAsync();

        Assert.Empty(pass.Core.Ingested);
        Assert.Equal(mark, (await pass.StoredAsync()).V3?.BackstopWatermarkUtc);
    }

    // A save that moves the address starts a new instance's history. Writing the instant read from the
    // old instance onto the new record would put every older record out of reach, with nothing
    // observable happening.
    [Fact]
    public async Task APassWhoseAddressMovedUnderItDoesNotMarkTheNewInstance()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-5));
        pass.Answering(Page(Descending(3)));
        pass.SavingDuringTheWalk(Aiming(MovedAddress));

        var result = await pass.RunAsync();

        var stored = await pass.StoredAsync();
        Assert.Equal(Noon, result.Watermark);
        Assert.Equal(MovedAddress, stored.V3?.Address);
        Assert.Null(stored.V3?.BackstopWatermarkUtc);
    }

    // The failure streak is built first, so its reset is an observation rather than the value an
    // untouched aggregate already holds.
    [Fact]
    public async Task APassWhoseAddressMovedUnderItStillRecordsThatItRan()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-30));
        pass.Answering(Page([Noon.AddMinutes(-5), Noon]), Page(Descending(3)));

        await pass.RunAsync();
        Assert.Equal(1, (await pass.StoredAsync()).ImportHealth.ConsecutiveFailures);

        pass.SavingDuringTheWalk(Aiming(MovedAddress));
        var walked = await pass.RunAsync();

        var stored = await pass.StoredAsync();
        Assert.Equal(BackstopPassOutcome.Walked, walked.Outcome);
        Assert.Null(stored.V3?.BackstopWatermarkUtc);
        Assert.Equal(0, stored.ImportHealth.ConsecutiveFailures);
        Assert.Equal("", stored.ImportHealth.LastError);
        Assert.False(stored.ImportHealth.BackstopPositionLost);
    }

    // Control for the two cases above: a refusal keyed on a write having met the pass, rather than on
    // the instance having changed, would satisfy both and stop the backstop advancing at all.
    [Fact]
    public async Task APassWhoseAddressStayedRecordsItsMarkThroughACompetingSave()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-5));
        pass.Answering(Page(Descending(3)));
        pass.SavingDuringTheWalk(Aiming(Address) with { UpgradeBehavior = UpgradeBehavior.Replace });

        await pass.RunAsync();

        var stored = await pass.StoredAsync();
        Assert.Equal(UpgradeBehavior.Replace, stored.UpgradeBehavior);
        Assert.Equal(Noon, stored.V3?.BackstopWatermarkUtc);
    }

    // The host's enqueue deduplicates nothing and defaults to exclusive, so one scan per record would
    // serialise three library scans behind each other.
    [Fact]
    public async Task APassThatImportedThreeFilesStartsOneScanCoveringAllThree()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-5));
        pass.Answering(PageOfDistinctPaths(3));

        await pass.RunAsync();

        Assert.Equal(3, pass.Core.Ingested.Count);
        Assert.Equal(
            pass.Core.Ingested.Select(candidate => candidate.ReportedPath).Order(),
            Assert.Single(pass.Library.Scans).Order());
    }

    // Control for the test above: without it, a pass that started a scan on every run would satisfy the
    // same "exactly one scan" assertion.
    [Fact]
    public async Task APassThatImportedNothingStartsNoScan()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-30));
        pass.Answering(Page([]));

        await pass.RunAsync();

        Assert.Empty(pass.Library.Scans);
    }

    // The identifier the candidate carries lives on the entity that argument asks for, so a walk asking
    // the other lineage's spelling would read a page with no entity on it.
    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public async Task TheWalkAsksForTheConnectedLineagesEntity(WhisparrGeneration generation)
    {
        var pass = new Pass(mark: Noon.AddMinutes(-5), generation: generation);
        pass.Answering(Page(Descending(3)));

        await pass.RunAsync();

        Assert.All(pass.Client.Histories, call => Assert.Equal(generation, call.Generation));
        Assert.NotEmpty(pass.Client.Histories);
    }

    // This is what lets an arrival through this channel re-point onto the item a webhook arrival for
    // the same scene would have found.
    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public async Task ARecordNamingASceneReachesTheCoreCarryingItsIdentifier(
        WhisparrGeneration generation)
    {
        var pass = new Pass(mark: Noon.AddMinutes(-5), generation: generation);
        pass.Answering(PageNamingAScene(generation));

        await pass.RunAsync();

        Assert.Equal(SceneIdentifier, Assert.Single(pass.Core.Ingested).RemoteId);
    }

    // Control for the case above: a file an instance never matched is still one to register.
    [Fact]
    public async Task ARecordNamingNoSceneStillReachesTheCore()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-5));
        pass.Answering(Page(Descending(1)));

        await pass.RunAsync();

        Assert.Null(Assert.Single(pass.Core.Ingested).RemoteId);
    }

    [Fact]
    public async Task TwoRecordsSharingOneInstantAreBothProjected()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-1));
        pass.Answering(Page([Noon, Noon]));

        await pass.RunAsync();

        Assert.Equal(2, pass.Core.Ingested.Count);
    }

    // Importing from an order the walk does not understand is how a bulk replay starts, so the pass
    // refuses and keeps its place rather than reading on.
    [Fact]
    public async Task AnAscendingPageRefusesThePassAndImportsNothing()
    {
        var mark = Noon.AddMinutes(-30);
        var pass = new Pass(mark);
        pass.Answering(Page([Noon.AddMinutes(-5), Noon]));

        var result = await pass.RunAsync();

        Assert.Equal(BackstopPassOutcome.RefusedPageOrder, result.Outcome);
        Assert.Empty(pass.Core.Ingested);
        Assert.Null(result.Watermark);
        Assert.Equal(mark, (await pass.StoredAsync()).V3?.BackstopWatermarkUtc);
    }

    [Fact]
    public async Task ARefusedPassIsRecordedInTheHealthAggregate()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-30));
        pass.Answering(Page([Noon.AddMinutes(-5), Noon]));

        await pass.RunAsync();

        var health = (await pass.StoredAsync()).ImportHealth;
        Assert.Equal(1, health.ConsecutiveFailures);
        Assert.NotNull(health.LastFailedAtUtc);
        Assert.NotEmpty(health.LastError);
    }

    // The last-failed instant is left where it was. It records that a failure happened, and a later
    // readout has to be able to say when.
    [Fact]
    public async Task AWalkedPassClearsTheFailureStreakAndKeepsWhenTheFailureHappened()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-30));
        pass.Answering(Page([Noon.AddMinutes(-5), Noon]), Page(Descending(3)));

        var refused = await pass.RunAsync();
        Assert.Equal(BackstopPassOutcome.RefusedPageOrder, refused.Outcome);
        var failed = (await pass.StoredAsync()).ImportHealth.LastFailedAtUtc;
        Assert.NotNull(failed);

        var walked = await pass.RunAsync();

        Assert.Equal(BackstopPassOutcome.Walked, walked.Outcome);
        var health = (await pass.StoredAsync()).ImportHealth;
        Assert.Equal(0, health.ConsecutiveFailures);
        Assert.Equal("", health.LastError);
        Assert.Equal(failed, health.LastFailedAtUtc);
    }

    [Fact]
    public async Task ThePositionLostFlagAFirstConnectRaisesIsClearedByTheNextWalk()
    {
        var pass = new Pass(mark: null);
        pass.Answering(Page(Descending(3)));

        await pass.RunAsync();
        Assert.True((await pass.StoredAsync()).ImportHealth.BackstopPositionLost);

        var walked = await pass.RunAsync();

        Assert.Equal(BackstopPassOutcome.Walked, walked.Outcome);
        Assert.False((await pass.StoredAsync()).ImportHealth.BackstopPositionLost);
    }

    [Fact]
    public async Task AWalkedPassThatImportedNothingStillCountsAsTheChannelWorking()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-30));
        pass.Answering(Page([Noon.AddMinutes(-5), Noon]), Page([]));

        await pass.RunAsync();
        var walked = await pass.RunAsync();

        Assert.Equal(BackstopPassOutcome.Walked, walked.Outcome);
        Assert.Empty(pass.Core.Ingested);
        Assert.Equal(0, (await pass.StoredAsync()).ImportHealth.ConsecutiveFailures);
    }

    // The order is read across the page boundary as well as within a page, so a second page starting
    // newer than the first one ended is the same refusal as a page that ascends.
    [Fact]
    public async Task ARouteThatDoesNotPageRefusesRatherThanWalkingForever()
    {
        var pass = new Pass(mark: Noon.AddYears(-1));
        pass.Answering(FullPage(0));

        var result = await pass.RunAsync();

        Assert.Equal(BackstopPassOutcome.RefusedPageOrder, result.Outcome);
        Assert.Equal(2, result.PagesRead);
    }

    // The across-page order check passes for this shape and no other: a repeated page starts newer than
    // the previous one ended unless every record on it carries the same instant. These records carry no
    // id, so the repeat is read off the range the two pages share.
    // The read count is bounded, so a rule that stops terminating raises here rather than running until
    // the suite is killed.
    [Fact]
    public async Task ARouteAnsweringOnePageOfOneInstantRefusesRatherThanWalkingForever()
    {
        var mark = Noon.AddYears(-1);
        var pass = new Pass(mark, requestBudget: 8);
        pass.Answering(Page(Enumerable.Repeat(Noon, BackstopPass.PageSize)));

        var result = await pass.RunAsync();

        Assert.Equal(BackstopPassOutcome.RefusedPageOrder, result.Outcome);
        Assert.Equal(2, result.PagesRead);
        Assert.Equal(mark, (await pass.StoredAsync()).V3?.BackstopWatermarkUtc);
    }

    // The same page answered again is the one shape a walk cannot read its way out of, and the records
    // past it are only reachable through a route that will not serve them.
    [Fact]
    public async Task ARouteAnsweringOnePageOfIdentifiedRecordsRefusesRatherThanWalkingForever()
    {
        var mark = Noon.AddYears(-1);
        var pass = new Pass(mark, requestBudget: 8);
        pass.Answering(PageOf(AtOneInstant(NewestRecordId, BackstopPass.PageSize)));

        var result = await pass.RunAsync();

        Assert.Equal(BackstopPassOutcome.RefusedPageOrder, result.Outcome);
        Assert.Equal(2, result.PagesRead);
        Assert.Equal(mark, (await pass.StoredAsync()).V3?.BackstopWatermarkUtc);
    }

    // The route is offset-paged, so records arriving mid-walk push the window back and the next page
    // begins inside the one already read. A long catch-up while the instance is still importing is
    // exactly when the pass has to keep going.
    [Fact]
    public async Task RecordsAddedAtTheHeadDuringTheWalkDoNotRefuseThePass()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-60));
        pass.Answering(
            PageOf(History(0, BackstopPass.PageSize)),
            PageOf(History(BackstopPass.PageSize - 3, BackstopPass.PageSize)));

        var result = await pass.RunAsync();

        Assert.Equal(BackstopPassOutcome.Walked, result.Outcome);
        Assert.Equal(Noon, (await pass.StoredAsync()).V3?.BackstopWatermarkUtc);
        Assert.Equal(
            [.. History(0, 61).Select(record => PathOf(record.Id))],
            pass.Core.Ingested.Select(candidate => candidate.ReportedPath));
    }

    // A bulk import of more than one page of records at one instant produces two pages with the same
    // newest and oldest instant and no record in common, which is a continuing run rather than a page
    // answered twice.
    [Fact]
    public async Task ATieRunSpanningAPageBoundaryIsWalkedThrough()
    {
        var pass = new Pass(mark: Noon.AddYears(-1), requestBudget: 8);
        pass.Answering(
            PageOf(AtOneInstant(NewestRecordId, BackstopPass.PageSize)),
            PageOf(AtOneInstant(NewestRecordId - BackstopPass.PageSize, BackstopPass.PageSize)),
            Page([]));

        var result = await pass.RunAsync();

        Assert.Equal(BackstopPassOutcome.Walked, result.Outcome);
        Assert.Equal(2 * BackstopPass.PageSize, result.RecordsTaken);
    }

    // The streak tells one bad pass from a channel that is stuck. Past the point where it has said
    // that, a number that keeps rising reports a condition getting worse while nothing has changed.
    [Fact]
    public async Task ARefusalThatRepeatsStopsClimbingRatherThanReadingAsAWorseningOutage()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-30));
        pass.Answering(Page([Noon.AddMinutes(-5), Noon]));

        await RefusedRepeatedlyAsync(pass, ImportHealthAggregate.ConsecutiveFailuresCeiling);
        var streak = (await pass.StoredAsync()).ImportHealth.ConsecutiveFailures;

        await RefusedRepeatedlyAsync(pass, ImportHealthAggregate.ConsecutiveFailuresCeiling);

        Assert.Equal(ImportHealthAggregate.ConsecutiveFailuresCeiling, streak);
        Assert.Equal(streak, (await pass.StoredAsync()).ImportHealth.ConsecutiveFailures);
    }

    private static async Task RefusedRepeatedlyAsync(Pass pass, int passes)
    {
        for (var attempt = 0; attempt < passes; attempt++)
        {
            Assert.Equal(BackstopPassOutcome.RefusedPageOrder, (await pass.RunAsync()).Outcome);
        }
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    public async Task AnAnswerThatIsNotAPageRefusesThePass(string body)
    {
        var pass = new Pass(mark: Noon.AddMinutes(-30));
        pass.Answering(RecordingWhisparrClient.Json(200, body));

        var result = await pass.RunAsync();

        Assert.Equal(BackstopPassOutcome.RefusedUnreadableAnswer, result.Outcome);
        Assert.Empty(pass.Core.Ingested);
    }

    // The mark means history up to here has been read, so a record that could not be taken is not a
    // page that was not read. A walk that ended in the throw would leave the mark where it was, and
    // every later pass would read the same page and fail on the same record for ever.
    // Asserted on the stored mark rather than on a call count: a mark write that ran and wrote nothing
    // would satisfy a count.
    [Fact]
    public async Task ARecordWhoseIngestThrowsDoesNotAbortTheWalkOrFreezeTheMark()
    {
        var mark = Noon.AddMinutes(-5);
        var pass = new Pass(mark);
        pass.Answering(PageOfDistinctPaths(3));
        pass.Core.ThrowFor("/whisparr-media/scene1.mp4", new InvalidOperationException());

        var result = await pass.RunAsync();

        Assert.Equal(BackstopPassOutcome.Walked, result.Outcome);
        Assert.Equal(3, result.RecordsTaken);
        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.Contained);
        Assert.Equal(Noon, result.Watermark);
        Assert.Equal(Noon, (await pass.StoredAsync()).V3?.BackstopWatermarkUtc);
        Assert.NotEqual(mark, (await pass.StoredAsync()).V3?.BackstopWatermarkUtc);
    }

    // The pages were read either way, which is what the mark records. Leaving it behind would make the
    // next pass re-read exactly the records that already failed.
    // The stored count and instant are asserted with the mark, because it is the mark moving that puts
    // those records beyond this channel. Without them the aggregate reports a pass that reached the
    // instance, cleared its failure streak and found nothing to take.
    [Fact]
    public async Task AWalkWhoseEveryRecordFailedStillAdvancesTheMark()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-5));
        pass.Answering(PageOfDistinctPaths(3));
        pass.Core.ThrowForEverything(new FileNotFoundException());

        var result = await pass.RunAsync();

        Assert.Equal(BackstopPassOutcome.Walked, result.Outcome);
        Assert.Equal(0, result.Imported);
        Assert.Equal(3, result.Contained);
        var stored = await pass.StoredAsync();
        Assert.Equal(Noon, stored.V3?.BackstopWatermarkUtc);
        Assert.Equal(3, stored.ImportHealth.RecordsContained);
        Assert.Equal(Pass.Now, stored.ImportHealth.LastContainedAtUtc);
        Assert.Empty(pass.Library.Scans);
    }

    // Control for the case above. A fold writing the count and the instant whatever the walk contained
    // satisfies that one, and every clean pass then reads as one that skipped records.
    [Fact]
    public async Task AWalkThatTookEveryRecordRecordsNoContainment()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-5));
        pass.Answering(PageOfDistinctPaths(3));

        var result = await pass.RunAsync();

        Assert.Equal(3, result.Imported);
        Assert.Equal(0, result.Contained);
        var health = (await pass.StoredAsync()).ImportHealth;
        Assert.Equal(0, health.RecordsContained);
        Assert.Null(health.LastContainedAtUtc);
    }

    // Every counted record is one the mark has already moved past, so no later pass has anything to
    // clear and a total that reset would report the backlog as smaller than it is.
    [Fact]
    public async Task TheContainmentTotalCarriesAcrossPasses()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-5));
        pass.Answering(PageOfDistinctPaths(3), Page([Noon.AddMinutes(2), Noon.AddMinutes(1)]));
        pass.Core.ThrowForEverything(new FileNotFoundException());

        var first = await pass.RunAsync();
        var second = await pass.RunAsync();

        Assert.Equal(3, first.Contained);
        Assert.Equal(2, second.Contained);
        Assert.Equal(5, (await pass.StoredAsync()).ImportHealth.RecordsContained);
    }

    // The total that instant belongs to does not clear, so clearing the instant alone would leave a
    // count of records passed over with no when. A pass runs on a timer, so the next one would clear it
    // almost at once.
    [Fact]
    public async Task APassThatContainedNothingKeepsWhenTheLastContainmentHappened()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-5));
        pass.Answering(PageOfDistinctPaths(3), Page([Noon.AddMinutes(2), Noon.AddMinutes(1)]));
        pass.Core.ThrowFor("/whisparr-media/scene0.mp4", new InvalidOperationException());

        var first = await pass.RunAsync();
        var second = await pass.RunAsync();

        Assert.Equal(1, first.Contained);
        Assert.Equal(0, second.Contained);
        var health = (await pass.StoredAsync()).ImportHealth;
        Assert.Equal(1, health.RecordsContained);
        Assert.Equal(Pass.Now, health.LastContainedAtUtc);
    }

    [Fact]
    public async Task ARecordWithNoReadablePathIsCounted()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-1));
        pass.Answering(Page([Noon], path: null));

        var result = await pass.RunAsync();

        Assert.Equal(1, result.WithoutCandidate);
        Assert.Empty(pass.Core.Ingested);
    }

    [Fact]
    public async Task AnUnconfiguredGenerationMakesNoRequest()
    {
        var pass = new Pass(mark: null, address: "");

        var result = await pass.RunAsync();

        Assert.Equal(BackstopPassOutcome.NotConfigured, result.Outcome);
        Assert.Empty(pass.Client.Verbs);
    }

    // Asserted as the set of verbs the pass did use rather than as a list of the ones it avoided: a verb
    // added to the seam and then called here is a failure rather than an omission from a list.
    [Fact]
    public async Task TheWholePassMakesOnlyHistoryReads()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-125));
        pass.Answering(FullPage(0), FullPage(1), FullPage(2));

        await pass.RunAsync();

        Assert.NotEmpty(pass.Client.Verbs);
        Assert.All(
            pass.Client.Verbs,
            verb => Assert.Equal(nameof(IWhisparrClient.ReadHistoryAsync), verb));
        Assert.Empty(pass.Client.Notifications);
    }

    // The walk may be linear in time. What it hands back must not be, and neither may what it holds
    // while walking.
    [Fact]
    public async Task AThousandRecordsLeaveAnAnswerOfTheSameSizeAsThree()
    {
        var pages = new WhisparrResponse[21];
        for (var page = 0; page < 20; page++)
        {
            pages[page] = FullPage(page);
        }

        pages[20] = Page([]);

        var many = new Pass(mark: Noon.AddYears(-1));
        many.Answering(pages);
        var overAThousand = await many.RunAsync();

        var few = new Pass(mark: Noon.AddYears(-1));
        few.Answering(Page(Descending(3)));
        var overThree = await few.RunAsync();

        Assert.Equal(20 * BackstopPass.PageSize, overAThousand.RecordsTaken);
        Assert.Equal(3, overThree.RecordsTaken);
        Assert.Equal(
            Serialized(overThree with { RecordsTaken = 0, Imported = 0, PagesRead = 0 }),
            Serialized(overAThousand with { RecordsTaken = 0, Imported = 0, PagesRead = 0 }));
    }

    // The structural half of the assertion above: a pass that accumulated records would answer with the
    // same counters and still hold the library in memory.
    [Fact]
    public void NeitherThePassNorItsAnswerDeclaresACollection()
    {
        Assert.DoesNotContain(
            typeof(BackstopPassResult).GetProperties(),
            property => IsCollection(property.PropertyType));

        Assert.DoesNotContain(
            typeof(BackstopPass).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
            field => IsCollection(field.FieldType));
    }

    private static bool IsCollection(Type type)
        => type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);

    // A record's own ToString renders a collection member as its type name, which is the same length
    // whatever the member holds, so a result that accumulated the library would render at the size of
    // one that accumulated nothing.
    private static string Serialized(BackstopPassResult result) => JsonSerializer.Serialize(result);

    private static WhisparrSyncSettingsSaveRequest Aiming(string address)
        => new(
            WhisparrGeneration.V3,
            new WhisparrSyncGenerationSaveRequest(address, KeyWriteSignal.Keep, null),
            null);

    private static DateTimeOffset[] Descending(int count)
        => [.. Enumerable.Range(0, count).Select(index => Noon.AddMinutes(-index))];

    // One record per minute with ids descending alongside the instants, so a page read after records
    // arrived at the head is the same run read from a later offset.
    private static (int Id, DateTimeOffset Instant)[] History(int from, int count)
        => [.. Enumerable
            .Range(from, count)
            .Select(offset => (NewestRecordId - offset, Noon.AddMinutes(-offset)))];

    private static (int Id, DateTimeOffset Instant)[] AtOneInstant(int firstId, int count)
        => [.. Enumerable.Range(0, count).Select(index => (firstId - index, Noon))];

    private static string PathOf(int id)
        => string.Create(CultureInfo.InvariantCulture, $"/whisparr-media/scene{id}.mp4");

    private static WhisparrResponse PageOf(IEnumerable<(int Id, DateTimeOffset Instant)> records)
    {
        var page = new JsonArray();
        foreach (var (id, instant) in records)
        {
            page.Add(
                new JsonObject
                {
                    ["id"] = id,
                    ["eventType"] = HistoryProjector.ImportedEventType,
                    ["date"] = instant.ToString("O"),
                    ["data"] = new JsonObject { ["importedPath"] = PathOf(id) },
                });
        }

        return RecordingWhisparrClient.Json(
            200, new JsonObject { ["records"] = page }.ToJsonString());
    }

    private static WhisparrResponse FullPage(int page)
        => Page(
            Enumerable
                .Range(page * BackstopPass.PageSize, BackstopPass.PageSize)
                .Select(index => Noon.AddMinutes(-index)));

    private static WhisparrResponse PageOfDistinctPaths(int count)
    {
        var records = new JsonArray();
        for (var index = 0; index < count; index++)
        {
            records.Add(
                new JsonObject
                {
                    ["eventType"] = HistoryProjector.ImportedEventType,
                    ["date"] = Noon.AddMinutes(-index).ToString("O"),
                    ["data"] = new JsonObject
                    {
                        ["importedPath"] = string.Create(
                            CultureInfo.InvariantCulture, $"/whisparr-media/scene{index}.mp4"),
                    },
                });
        }

        return RecordingWhisparrClient.Json(
            200, new JsonObject { ["records"] = records }.ToJsonString());
    }

    private static WhisparrResponse PageNamingAScene(WhisparrGeneration generation)
    {
        var (entity, member) = generation == WhisparrGeneration.V3
            ? ("movie", "stashId")
            : ("episode", "tvdbId");

        var record = new JsonObject
        {
            ["eventType"] = HistoryProjector.ImportedEventType,
            ["date"] = Noon.ToString("O"),
            ["data"] = new JsonObject { ["importedPath"] = ImportedPath },
            [entity] = new JsonObject { [member] = SceneIdentifier },
        };

        return RecordingWhisparrClient.Json(
            200, new JsonObject { ["records"] = new JsonArray(record) }.ToJsonString());
    }

    private static WhisparrResponse Page(
        IEnumerable<DateTimeOffset> instants, string? path = ImportedPath)
    {
        var records = new JsonArray();
        foreach (var instant in instants)
        {
            records.Add(
                new JsonObject
                {
                    ["eventType"] = HistoryProjector.ImportedEventType,
                    ["date"] = instant.ToString("O"),
                    ["data"] = path is null
                        ? new JsonObject()
                        : new JsonObject { ["importedPath"] = path },
                });
        }

        return RecordingWhisparrClient.Json(
            200, new JsonObject { ["records"] = records }.ToJsonString());
    }

    private sealed class Pass
    {
        public static readonly DateTimeOffset Now = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

        private readonly OptionsStore _options;
        private readonly int? _requestBudget;
        private WhisparrSyncSettingsSaveRequest? _competing;

        public Pass(
            DateTimeOffset? mark,
            string address = Address,
            WhisparrGeneration generation = WhisparrGeneration.V3,
            int? requestBudget = null)
        {
            Generation = generation;
            _requestBudget = requestBudget;
            Client = new RecordingWhisparrClient(RecordingWhisparrClient.Json(200, """{"records":[]}"""));
            _options = new OptionsStore(Store);
            _options
                .SaveAsync(
                    new WhisparrSyncOptions { SelectedGeneration = generation }.WithConnectionFor(
                        generation,
                        new WhisparrSyncGenerationConnection
                        {
                            Address = address,
                            BackstopWatermarkUtc = mark,
                        }),
                    TestContext.Current.CancellationToken)
                .GetAwaiter()
                .GetResult();
        }

        public WhisparrGeneration Generation { get; }

        public FakeStore Store { get; } = new();

        public OptionsWriteGate Gate { get; } = new();

        public RecordingWhisparrClient Client { get; }

        public FollowUpScanCoalescer FollowUp { get; } = new(new FixedClock(Now), NullLogger.Instance);

        public RecordingLibrary Library { get; } = new(reached: true, ["/data"]);

        public RecordingImportCore Core => _core ??= new(FollowUp, Library);

        private RecordingImportCore? _core;

        public void Answering(params WhisparrResponse[] pages)
            => Client.Answering(nameof(IWhisparrClient.ReadHistoryAsync), pages);

        // The competing write is the production one rather than a stand-in, so what the fold at the end of
        // the walk meets is what a save landing mid-walk really leaves behind.
        public void SavingDuringTheWalk(WhisparrSyncSettingsSaveRequest save) => _competing = save;

        public Task<BackstopPassResult> RunAsync()
            => new BackstopPass(
                    ClientForRun(),
                    _options,
                    Gate,
                    new RecordingCredentialPort().Holding(Generation, ApiKey),
                    Core,
                    new FixedClock(Now),
                    FollowUp,
                    Library,
                    NullLogger.Instance)
                .RunAsync(TestContext.Current.CancellationToken);

        public Task<WhisparrSyncOptions> StoredAsync()
            => _options.LoadAsync(TestContext.Current.CancellationToken);

        private IWhisparrClient ClientForRun()
        {
            IWhisparrClient client = _requestBudget is { } budget
                ? new BoundedClient(Client, budget)
                : Client;
            return _competing is { } save ? new SavingClient(client, () => CommitAsync(save)) : client;
        }

        private Task<WhisparrSyncOptions> CommitAsync(WhisparrSyncSettingsSaveRequest save)
            => Gate.MutateAsync(
                _options,
                stored => SettingsProjector.Apply(stored, save),
                TestContext.Current.CancellationToken);
    }

    // The other verbs raise: a pass makes history reads and nothing else, so a call reaching one of them
    // is a change to what a pass does rather than a gap here.
    private sealed class SavingClient(IWhisparrClient inner, Func<Task> save) : IWhisparrClient
    {
        private bool _committed;

        public async Task<WhisparrResponse> ReadHistoryAsync(
            Uri baseAddress,
            string apiKey,
            WhisparrGeneration generation,
            int page,
            int pageSize,
            CancellationToken ct)
        {
            if (!_committed)
            {
                _committed = true;
                await save().ConfigureAwait(false);
            }

            return await inner
                .ReadHistoryAsync(baseAddress, apiKey, generation, page, pageSize, ct)
                .ConfigureAwait(false);
        }

        public Task<WhisparrResponse> ReadStatusAsync(
            Uri baseAddress, string apiKey, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<WhisparrResponse> ReadNotificationSchemaAsync(
            Uri baseAddress, string apiKey, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<WhisparrResponse> ListNotificationsAsync(
            Uri baseAddress, string apiKey, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<WhisparrResponse> ReadRootFoldersAsync(
            Uri baseAddress, string apiKey, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<WhisparrResponse> ReadQualityProfilesAsync(
            Uri baseAddress, string apiKey, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<WhisparrResponse> ReadCommandAsync(
            Uri baseAddress, string apiKey, int commandId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<WhisparrResponse> CreateNotificationAsync(
            Uri baseAddress, string apiKey, JsonNode body, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<WhisparrResponse> UpdateNotificationAsync(
            Uri baseAddress, string apiKey, int id, JsonNode body, CancellationToken ct)
            => throw new NotSupportedException();
    }

    // It notes each import into the coalescer as the real core does.
    private sealed class RecordingImportCore(FollowUpScanCoalescer followUp, ICoveLibraryPort library)
        : IImportCore
    {
        private readonly Dictionary<string, Exception> _raised = [];
        private Exception? _raisedForEverything;

        public List<ImportCandidate> Ingested { get; } = [];

        public void ThrowFor(string path, Exception failure) => _raised[path] = failure;

        public void ThrowForEverything(Exception failure) => _raisedForEverything = failure;

        public Task<ImportOutcome> IngestAsync(ImportCandidate candidate, CancellationToken ct)
        {
            Ingested.Add(candidate);
            var failure = _raisedForEverything ?? _raised.GetValueOrDefault(candidate.ReportedPath);
            if (failure is not null)
            {
                return Task.FromException<ImportOutcome>(failure);
            }

            followUp.NoteImported(candidate.ReportedPath, library);
            return Task.FromResult(ImportOutcome.Imported);
        }
    }

    // The raise is of a kind the walk classifies as neither unreachable nor cancelled, so it leaves the
    // pass instead of being recorded as a refusal. That is what makes a non-terminating walk a failing
    // case rather than a hanging one.
    private sealed class BoundedClient(RecordingWhisparrClient inner, int budget) : IWhisparrClient
    {
        private int _reads;

        public Task<WhisparrResponse> ReadHistoryAsync(
            Uri baseAddress,
            string apiKey,
            WhisparrGeneration generation,
            int page,
            int pageSize,
            CancellationToken ct)
        {
            _reads++;
            return _reads > budget
                ? throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The walk asked for more than {budget} pages."))
                : inner.ReadHistoryAsync(baseAddress, apiKey, generation, page, pageSize, ct);
        }

        public Task<WhisparrResponse> ReadStatusAsync(Uri baseAddress, string apiKey, CancellationToken ct)
            => inner.ReadStatusAsync(baseAddress, apiKey, ct);

        public Task<WhisparrResponse> ReadNotificationSchemaAsync(
            Uri baseAddress, string apiKey, CancellationToken ct)
            => inner.ReadNotificationSchemaAsync(baseAddress, apiKey, ct);

        public Task<WhisparrResponse> ListNotificationsAsync(
            Uri baseAddress, string apiKey, CancellationToken ct)
            => inner.ListNotificationsAsync(baseAddress, apiKey, ct);

        public Task<WhisparrResponse> ReadRootFoldersAsync(
            Uri baseAddress, string apiKey, CancellationToken ct)
            => inner.ReadRootFoldersAsync(baseAddress, apiKey, ct);

        public Task<WhisparrResponse> ReadQualityProfilesAsync(
            Uri baseAddress, string apiKey, CancellationToken ct)
            => inner.ReadQualityProfilesAsync(baseAddress, apiKey, ct);

        public Task<WhisparrResponse> ReadCommandAsync(
            Uri baseAddress, string apiKey, int commandId, CancellationToken ct)
            => inner.ReadCommandAsync(baseAddress, apiKey, commandId, ct);

        public Task<WhisparrResponse> CreateNotificationAsync(
            Uri baseAddress, string apiKey, JsonNode body, CancellationToken ct)
            => inner.CreateNotificationAsync(baseAddress, apiKey, body, ct);

        public Task<WhisparrResponse> UpdateNotificationAsync(
            Uri baseAddress, string apiKey, int id, JsonNode body, CancellationToken ct)
            => inner.UpdateNotificationAsync(baseAddress, apiKey, id, body, ct);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
