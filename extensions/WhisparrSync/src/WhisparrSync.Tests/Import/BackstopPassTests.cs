using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Import;

/// <summary>
/// One walk back through an instance's history: how far it reads, what it hands to the ingest core,
/// where it leaves the mark, and what it never does.
/// </summary>
/// <remarks>
/// The instance under test always HAS a past. Against an empty one, "the pass imported nothing" and
/// "the pass did nothing" are the same observation, and every assertion below would hold with the
/// walk deleted.
/// </remarks>
public sealed class BackstopPassTests
{
    private const string Address = "http://whisparr:6969";
    private const string MovedAddress = "http://whisparr-elsewhere:6969";
    private const string ApiKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";
    private const string ImportedPath = "/whisparr-media/scene.mp4";

    /// <summary>
    /// The identifier the entity on a record names, written so both lineages can carry it as text.
    /// </summary>
    private const string SceneIdentifier = "4149372";

    private static readonly DateTimeOffset Noon = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// With no mark stored, the pass records where history ends and imports nothing.
    /// </summary>
    /// <remarks>
    /// Both halves are asserted. The instance holds records the pass could have imported, so the mark
    /// advancing is what tells "imported nothing" from "read nothing".
    /// </remarks>
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

    /// <summary>The first pass records that the position was lost, so a gap may exist.</summary>
    [Fact]
    public async Task TheFirstPassRecordsThePositionAsLost()
    {
        var pass = new Pass(mark: null);
        pass.Answering(Page(Descending(3)));

        await pass.RunAsync();

        Assert.True((await pass.StoredAsync()).ImportHealth.BackstopPositionLost);
    }

    /// <summary>An instance with no history at all still leaves the unset state.</summary>
    /// <remarks>
    /// Without a mark written here, every later pass would be another first connect and the backstop
    /// would never import anything.
    /// </remarks>
    [Fact]
    public async Task AFirstPassOverAnEmptyHistoryStillWritesAMark()
    {
        var pass = new Pass(mark: null);
        pass.Answering(Page([]));

        await pass.RunAsync();

        Assert.Equal(Pass.Now, (await pass.StoredAsync()).V3?.BackstopWatermarkUtc);
    }

    /// <summary>The walk asks only for the pages it has to read.</summary>
    /// <remarks>
    /// Five pages exist and the mark sits inside the third. A walk that read to the end of the
    /// history would ask for all five, and the records past the mark are ones it has already imported.
    /// </remarks>
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

    /// <summary>The mark moves to the newest record the walk saw, not to the oldest.</summary>
    [Fact]
    public async Task TheMarkMovesToTheNewestRecordTheWalkSaw()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-2));
        pass.Answering(Page(Descending(3)));

        var result = await pass.RunAsync();

        Assert.Equal(Noon, result.Watermark);
        Assert.Equal(Noon, (await pass.StoredAsync()).V3?.BackstopWatermarkUtc);
    }

    /// <summary>A pass with nothing new leaves the mark where it was.</summary>
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

    /// <summary>
    /// A save that moved the address under the walk leaves the new instance's mark unset.
    /// </summary>
    /// <remarks>
    /// A save that moves the address starts a new instance's history again, and the walk it lands
    /// inside is an unbounded number of outbound reads long. Writing the instant read from the old
    /// instance onto the new record would put every record older than it out of reach for good, with
    /// nothing observable happening.
    /// </remarks>
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

    /// <summary>
    /// A pass whose address moved under it still records that it ran, because the health half
    /// describes the pass and not a position in an instance's history.
    /// </summary>
    /// <remarks>
    /// The failure streak is built first, so its reset is an observation rather than the value an
    /// untouched aggregate already holds.
    /// </remarks>
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

    /// <summary>
    /// A save landing under the walk that left the address where it points still records the mark.
    /// </summary>
    /// <remarks>
    /// The discriminating control for the two cases above: a refusal keyed on a write having met the
    /// pass, rather than on the instance having changed, would satisfy both and stop the backstop
    /// advancing at all.
    /// </remarks>
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

    /// <summary>
    /// A pass that imported three files starts ONE scan at the end of it, covering all three.
    /// </summary>
    /// <remarks>
    /// The host's enqueue deduplicates nothing and defaults to exclusive, so one scan per record
    /// would serialise three library scans behind each other for one pass.
    /// </remarks>
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

    /// <summary>A pass that imported nothing starts no scan.</summary>
    /// <remarks>
    /// The discriminating control for the test above: without it, a pass that started a scan on every
    /// run would satisfy the same "exactly one scan" assertion.
    /// </remarks>
    [Fact]
    public async Task APassThatImportedNothingStartsNoScan()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-30));
        pass.Answering(Page([]));

        await pass.RunAsync();

        Assert.Empty(pass.Library.Scans);
    }

    /// <summary>
    /// The walk asks each page for the entity spelling of the lineage it is connected to.
    /// </summary>
    /// <remarks>
    /// The argument rather than the count: the identifier the candidate carries lives on the entity
    /// that argument asks for, so a walk asking the other lineage's spelling would read a page with no
    /// entity on it and hand the core a candidate matchable on nothing.
    /// </remarks>
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

    /// <summary>
    /// A record whose entity declares an identifier hands the ingest core a candidate carrying it.
    /// </summary>
    /// <remarks>
    /// This is what lets an arrival through this channel re-point onto the item a webhook arrival for
    /// the same scene would have found, rather than creating a second one beside it.
    /// </remarks>
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

    /// <summary>
    /// A record whose answer embedded no entity still reaches the core, carrying no identifier.
    /// </summary>
    /// <remarks>
    /// The discriminating control for the case above, and the residual this plan leaves in place: a
    /// file an instance never matched is still one to register.
    /// </remarks>
    [Fact]
    public async Task ARecordNamingNoSceneStillReachesTheCore()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-5));
        pass.Answering(Page(Descending(1)));

        await pass.RunAsync();

        Assert.Null(Assert.Single(pass.Core.Ingested).RemoteId);
    }

    /// <summary>Two records sharing one instant are both handed to the ingest core.</summary>
    [Fact]
    public async Task TwoRecordsSharingOneInstantAreBothProjected()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-1));
        pass.Answering(Page([Noon, Noon]));

        await pass.RunAsync();

        Assert.Equal(2, pass.Core.Ingested.Count);
    }

    /// <summary>
    /// A page whose records ascend refuses the pass, and nothing reaches the ingest core.
    /// </summary>
    /// <remarks>
    /// Importing from an order the walk does not understand is how a bulk replay starts, so the pass
    /// refuses and keeps its place rather than reading on.
    /// </remarks>
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

    /// <summary>A refused pass is recorded, not swallowed.</summary>
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

    /// <summary>
    /// A pass that walked resets the streak the refusals before it built, so the recorded health can
    /// improve as well as decline.
    /// </summary>
    /// <remarks>
    /// The last-failed instant is deliberately left where it was. It records that a failure happened,
    /// and a later readout has to be able to say when.
    /// </remarks>
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

    /// <summary>
    /// The lost-position flag a first connect raises is cleared by the next pass that walks, so it
    /// reports a gap that may exist rather than one that did once.
    /// </summary>
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

    /// <summary>
    /// A pass that reached the instance and imported nothing still counts as the channel working: it
    /// authenticated, read the history and advanced the mark.
    /// </summary>
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

    /// <summary>
    /// A route answering every page with the same one refuses rather than walking forever.
    /// </summary>
    /// <remarks>
    /// The order is read across the page boundary as well as within a page, so a second page starting
    /// newer than the first one ended is the same refusal as a page that ascends.
    /// </remarks>
    [Fact]
    public async Task ARouteThatDoesNotPageRefusesRatherThanWalkingForever()
    {
        var pass = new Pass(mark: Noon.AddYears(-1));
        pass.Answering(FullPage(0));

        var result = await pass.RunAsync();

        Assert.Equal(BackstopPassOutcome.RefusedPageOrder, result.Outcome);
        Assert.Equal(2, result.PagesRead);
    }

    /// <summary>
    /// A route answering every page with one page of a single instant refuses rather than walking
    /// forever.
    /// </summary>
    /// <remarks>
    /// The across-page order check passes for this shape and no other: a repeated page starts newer
    /// than the previous one ended unless every record on it carries the same instant. A genuine tie
    /// takes that same shape, so the two are told apart by whether the range repeats rather than by
    /// the order.
    /// <para>
    /// The read count is bounded, so a rule that stops terminating raises here rather than running
    /// until the suite is killed.
    /// </para>
    /// </remarks>
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

    /// <summary>An answer this product cannot read as a page refuses the pass.</summary>
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

    /// <summary>
    /// A record the ingest cannot take at all leaves the walk running and the mark moving.
    /// </summary>
    /// <remarks>
    /// The mark means "history up to here has been read", so a record that could not be taken is not
    /// a page that was not read. A walk that ended in the throw would leave the mark where it was,
    /// and every later pass would then read the same page and fail on the same record for ever - a
    /// channel that has silently stopped importing, which is the state it exists to prevent.
    /// <para>
    /// Asserted on the STORED mark after the pass rather than on a call count: a mark write that ran
    /// and wrote nothing would satisfy a count.
    /// </para>
    /// </remarks>
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

    /// <summary>A walk whose every record failed is still a walk, so the mark still moves.</summary>
    /// <remarks>
    /// The pages were read either way, which is what the mark records. Leaving it behind would make
    /// the next pass re-read exactly the records that already failed.
    /// </remarks>
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
        Assert.Equal(Noon, (await pass.StoredAsync()).V3?.BackstopWatermarkUtc);
        Assert.Empty(pass.Library.Scans);
    }

    /// <summary>A record naming an import with no path is counted rather than dropped.</summary>
    [Fact]
    public async Task ARecordWithNoReadablePathIsCounted()
    {
        var pass = new Pass(mark: Noon.AddMinutes(-1));
        pass.Answering(Page([Noon], path: null));

        var result = await pass.RunAsync();

        Assert.Equal(1, result.WithoutCandidate);
        Assert.Empty(pass.Core.Ingested);
    }

    /// <summary>A generation with no address and key reaches nothing that could make a request.</summary>
    [Fact]
    public async Task AnUnconfiguredGenerationMakesNoRequest()
    {
        var pass = new Pass(mark: null, address: "");

        var result = await pass.RunAsync();

        Assert.Equal(BackstopPassOutcome.NotConfigured, result.Outcome);
        Assert.Empty(pass.Client.Verbs);
    }

    /// <summary>
    /// The whole pass makes read calls and nothing else.
    /// </summary>
    /// <remarks>
    /// Asserted as the set of verbs the pass DID use rather than as a list of the ones it avoided: a
    /// verb added to the seam and then called here is a failure rather than an omission from a list.
    /// </remarks>
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

    /// <summary>
    /// A thousand records leave an answer the same size as three do.
    /// </summary>
    /// <remarks>
    /// The walk may be linear in time. What it hands back must not be, and neither may what it holds
    /// while walking.
    /// </remarks>
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
            RenderedSizeOf(overThree with { RecordsTaken = 0, Imported = 0, PagesRead = 0 }),
            RenderedSizeOf(overAThousand with { RecordsTaken = 0, Imported = 0, PagesRead = 0 }));
    }

    /// <summary>Neither the pass nor its answer declares a collection member.</summary>
    /// <remarks>
    /// The structural half of the assertion above: a pass that accumulated records would answer with
    /// the same counters and still hold the library in memory.
    /// </remarks>
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

    /// <summary>How long a result renders as, which grows with anything it accumulated.</summary>
    private static int RenderedSizeOf(BackstopPassResult result) => result.ToString().Length;

    /// <summary>A settings save aiming v3 at <paramref name="address"/>, leaving the key alone.</summary>
    private static WhisparrSyncSettingsSaveRequest Aiming(string address)
        => new(
            WhisparrGeneration.V3,
            new WhisparrSyncGenerationSaveRequest(address, KeyWriteSignal.Keep, null),
            null);

    private static DateTimeOffset[] Descending(int count)
        => [.. Enumerable.Range(0, count).Select(index => Noon.AddMinutes(-index))];

    /// <summary>One page's worth of records, one minute apart, descending from <paramref name="page"/>.</summary>
    private static WhisparrResponse FullPage(int page)
        => Page(
            Enumerable
                .Range(page * BackstopPass.PageSize, BackstopPass.PageSize)
                .Select(index => Noon.AddMinutes(-index)));

    /// <summary>One page of import records, each naming a different file.</summary>
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

    /// <summary>
    /// One page holding a single import record whose entity names <see cref="SceneIdentifier"/>, in
    /// the spelling <paramref name="generation"/>'s own instance answers with.
    /// </summary>
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

    /// <summary>One pass, its store, and the doubles standing in for everything outside it.</summary>
    private sealed class Pass
    {
        /// <summary>The instant this pass's clock reads.</summary>
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

        /// <summary>The lineage this pass is connected to.</summary>
        public WhisparrGeneration Generation { get; }

        public FakeStore Store { get; } = new();

        /// <summary>The one gate every write in a case goes through, as the container has it.</summary>
        public OptionsWriteGate Gate { get; } = new();

        public RecordingWhisparrClient Client { get; }

        /// <summary>The one pending batch this pass's imports collect into.</summary>
        public FollowUpScanCoalescer FollowUp { get; } = new(new FixedClock(Now), NullLogger.Instance);

        public RecordingLibrary Library { get; } = new(reached: true, ["/data"]);

        public RecordingImportCore Core => _core ??= new(FollowUp, Library);

        private RecordingImportCore? _core;

        /// <summary>Queues what each history read answers with, the last entry repeating.</summary>
        public void Answering(params WhisparrResponse[] pages)
            => Client.Answering(nameof(IWhisparrClient.ReadHistoryAsync), pages);

        /// <summary>
        /// Commits <paramref name="save"/> at the first history read of the next pass, through the
        /// gate and the projector the settings endpoint writes through.
        /// </summary>
        /// <remarks>
        /// The competing write is the production one rather than a stand-in, so what the fold at the
        /// end of the walk meets is what a save landing mid-walk really leaves behind.
        /// </remarks>
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

    /// <summary>A client that commits one settings save before it answers the first history read.</summary>
    /// <remarks>
    /// The other verbs raise: a pass makes history reads and nothing else, so a call reaching one of
    /// them is a change to what a pass does rather than a gap here.
    /// </remarks>
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

        public Task<WhisparrResponse> CreateNotificationAsync(
            Uri baseAddress, string apiKey, JsonNode body, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<WhisparrResponse> UpdateNotificationAsync(
            Uri baseAddress, string apiKey, int id, JsonNode body, CancellationToken ct)
            => throw new NotSupportedException();
    }

    /// <summary>An ingest core recording every candidate handed to it.</summary>
    /// <remarks>
    /// It notes each import into the coalescer, standing in for the real core, whose own note is
    /// asserted where that core is driven.
    /// </remarks>
    private sealed class RecordingImportCore(FollowUpScanCoalescer followUp, ICoveLibraryPort library)
        : IImportCore
    {
        private readonly Dictionary<string, Exception> _raised = [];
        private Exception? _raisedForEverything;

        public List<ImportCandidate> Ingested { get; } = [];

        /// <summary>Makes the ingest of the candidate naming <paramref name="path"/> raise.</summary>
        public void ThrowFor(string path, Exception failure) => _raised[path] = failure;

        /// <summary>Makes every ingest raise.</summary>
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

    /// <summary>A client that raises once a walk has asked for more pages than it was allowed.</summary>
    /// <remarks>
    /// The raise is of a kind the walk classifies as neither unreachable nor cancelled, so it leaves
    /// the pass instead of being recorded as a refusal. That is what makes a non-terminating walk a
    /// failing case rather than a hanging one.
    /// </remarks>
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
