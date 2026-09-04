using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// The run that offers one entity's own scenes to the connected instance, one at a time.
/// </summary>
/// <remarks>
/// Driven through the recorder that stands in for the whole outbound seam, so the ordered verb log
/// covers every request this product can issue rather than the ones one interface declares.
/// <para>
/// The classification of an already-held scene is read from the documents build 3.3.8.1097 produced.
/// Both answers carry the same status and the same content type, so a run classifying on the status
/// would report every scene the instance holds as a refusal.
/// </para>
/// </remarks>
public sealed class AddAllMissingPlannerTests
{
    private const string AlreadyHeldFixture = "whisparr-v3-3.3.8.1097-scene-add-already-held.json";

    private const string UnknownIdentifierFixture =
        "whisparr-v3-3.3.8.1097-scene-add-unknown-identifier.json";

    private const string AcceptedFixture = "whisparr-v3-3.3.8.1097-scene-add-accepted.json";

    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";
    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";
    private const string ThirdScene = "027393c9-e589-4548-8a7f-c04292a9de14";

    private static readonly Uri Instance = new("http://whisparr-v3:6969");

    private static readonly AddDefaults Defaults = new(1, "/config/library");

    /// <summary>A key of the shape the settings store holds one in.</summary>
    /// <remarks>
    /// Written down here rather than taken from the shared monitor host: that host owns a real Cove
    /// context, and this file compiles on the leg where those types are absent.
    /// </remarks>
    private const string StoredKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>
    /// Three identifiers are three registrations and one catalogue refresh, in that order.
    /// </summary>
    /// <remarks>
    /// The refresh comes after rather than before, because a registration becomes visible in the
    /// instance's own catalogue only once one has run.
    /// </remarks>
    [Fact]
    public async Task ThreeIdentifiersAreThreeRegistrationsThenOneCatalogueRefresh()
    {
        var client = Accepting();

        var run = await RunOver(client, [FirstScene, SecondScene, ThirdScene]);

        Assert.Equal(
            [
                nameof(IWhisparrMissingSceneActing.AddSceneAsync),
                nameof(IWhisparrMissingSceneActing.AddSceneAsync),
                nameof(IWhisparrMissingSceneActing.AddSceneAsync),
                nameof(IWhisparrMissingSceneActing.RefreshCatalogueAsync),
            ],
            client.Verbs);
        Assert.Equal(
            [FirstScene, SecondScene, ThirdScene],
            client.Acting
                .Where(call => call.Verb == nameof(IWhisparrMissingSceneActing.AddSceneAsync))
                .Select(call => call.ForeignId));
        Assert.Equal(AddAllMissingRunOutcome.Completed, run.Outcome);
        Assert.Equal(3, run.Registered);
    }

    /// <summary>
    /// A scene the instance already holds is counted as already held, never as a refusal.
    /// </summary>
    [Fact]
    public async Task ASceneTheInstanceAlreadyHoldsIsCountedAsHeldRatherThanRefused()
    {
        var client = new RecordingWhisparrClient(
            RecordingWhisparrClient.Json(400, ProbeFixtures.Read(AlreadyHeldFixture)));

        var run = await RunOver(client, [FirstScene, SecondScene]);

        Assert.Equal(0, run.Registered);
        Assert.Equal(2, run.AlreadyHeld);
        Assert.Equal(0, run.Refused);
    }

    /// <summary>
    /// The catalogue refresh runs even when the instance already held every scene.
    /// </summary>
    /// <remarks>
    /// What the run offered is not the only thing the refresh brings in: the instance re-reads its
    /// own metadata source for the entity, and the entity's catalogue is what the reader asked to be
    /// complete.
    /// </remarks>
    [Fact]
    public async Task ARunThatRegisteredNothingStillRefreshesTheCatalogue()
    {
        var client = new RecordingWhisparrClient(
            RecordingWhisparrClient.Json(400, ProbeFixtures.Read(AlreadyHeldFixture)));

        await RunOver(client, [FirstScene]);

        Assert.Contains(nameof(IWhisparrMissingSceneActing.RefreshCatalogueAsync), client.Verbs);
    }

    /// <summary>
    /// An identifier no provider lists is a refusal, and is told apart from a scene already held.
    /// </summary>
    /// <remarks>
    /// The control matters as much as the case: both answers carry the same status and the same
    /// content type, so a run that could not tell them apart would report a whole entity as already
    /// registered.
    /// </remarks>
    [Fact]
    public async Task AnIdentifierTheInstanceDoesNotRecogniseIsRefusedRatherThanHeld()
    {
        var client = new RecordingWhisparrClient(
            RecordingWhisparrClient.Json(400, ProbeFixtures.Read(UnknownIdentifierFixture)));

        var run = await RunOver(client, [FirstScene]);

        Assert.Equal(0, run.AlreadyHeld);
        Assert.Equal(1, run.Refused);
    }

    /// <summary>
    /// The three answers the pinned build gave are classified as three different things.
    /// </summary>
    /// <remarks>
    /// The status alone separates none of them: the already-held answer and the control share both
    /// the status and the content type, which is why the classification reads the error code.
    /// </remarks>
    [Fact]
    public void TheClassificationReadsTheErrorCodeRatherThanTheStatus()
    {
        Assert.Equal(
            SceneRegistration.Registered,
            AddAllMissingPlanner.Classify(201, ProbeFixtures.Read(AcceptedFixture)));
        Assert.Equal(
            SceneRegistration.AlreadyHeld,
            AddAllMissingPlanner.Classify(400, ProbeFixtures.Read(AlreadyHeldFixture)));
        Assert.Equal(
            SceneRegistration.Refused,
            AddAllMissingPlanner.Classify(400, ProbeFixtures.Read(UnknownIdentifierFixture)));
    }

    /// <summary>
    /// A body nothing can be read out of is a refusal rather than an already-held scene.
    /// </summary>
    /// <remarks>
    /// The claim that costs least when it is wrong. Reporting a scene as held that the instance
    /// refused leaves the reader believing a catalogue is complete when it is not.
    /// </remarks>
    [Fact]
    public void AnUnreadableAnswerIsRefusedRatherThanHeld()
    {
        Assert.Equal(SceneRegistration.Refused, AddAllMissingPlanner.Classify(500, "not json"));
        Assert.Equal(SceneRegistration.Refused, AddAllMissingPlanner.Classify(400, null));
        Assert.Equal(SceneRegistration.Refused, AddAllMissingPlanner.Classify(409, "[]"));
    }

    /// <summary>
    /// A registration that reached no answer at all is refused, and the run carries on.
    /// </summary>
    [Fact]
    public async Task ARegistrationThatReachedNoAnswerIsRefusedAndTheRunCarriesOn()
    {
        var client = Accepting();

        var run = await AddAllMissingPlanner.RunAsync(
            Identities([FirstScene, SecondScene]),
            (identity, ct) => identity == FirstScene
                ? Task.FromResult<WhisparrResponse?>(null)
                : Register(client, identity, ct),
            ct => Refresh(client, ct),
            TestCt);

        Assert.Equal(1, run.Refused);
        Assert.Equal(1, run.Registered);
        Assert.Equal(AddAllMissingRunOutcome.Completed, run.Outcome);
    }

    /// <summary>
    /// An answer larger than the read bound is refused, not registered, and the run carries on.
    /// </summary>
    /// <remarks>
    /// The bounded read answers a success status and an empty body and states the refusal on the
    /// answer itself, so a classification reading only the status and the body counts the scene as
    /// registered and tells the reader a catalogue is complete when it is not.
    /// </remarks>
    [Fact]
    public async Task AnAnswerLargerThanTheReadBoundIsRefusedRatherThanRegistered()
    {
        var client = Accepting();

        var run = await AddAllMissingPlanner.RunAsync(
            Identities([FirstScene, SecondScene]),
            (identity, ct) => identity == FirstScene
                ? Task.FromResult<WhisparrResponse?>(
                    new WhisparrResponse(200, "application/json", string.Empty)
                    {
                        Refusal = MonitorRefusalKind.AnswerTooLargeToRead,
                    })
                : Register(client, identity, ct),
            ct => Refresh(client, ct),
            TestCt);

        Assert.Equal(1, run.Refused);
        Assert.Equal(1, run.Registered);
        Assert.Equal(0, run.AlreadyHeld);
    }

    /// <summary>
    /// A run over no identifier at all sends nothing and says that there was nothing to register.
    /// </summary>
    /// <remarks>
    /// A distinct outcome rather than a completed run that did nothing. A job that did nothing still
    /// appears in the host's Job Drawer, where it reads as work that happened.
    /// </remarks>
    [Fact]
    public async Task ARunOverNoIdentifierSendsNothingAndSaysSo()
    {
        var client = Accepting();

        var run = await RunOver(client, []);

        Assert.Empty(client.Verbs);
        Assert.Equal(AddAllMissingRunOutcome.NothingToRegister, run.Outcome);
        Assert.Equal(0, run.Registered);
    }

    /// <summary>
    /// A run stopped part way is cancelled rather than failed, and keeps what it registered.
    /// </summary>
    [Fact]
    public async Task ARunStoppedPartWayIsCancelledAndKeepsWhatItRegistered()
    {
        var client = Accepting();
        using var stopping = new CancellationTokenSource();

        var run = await AddAllMissingPlanner.RunAsync(
            Identities([FirstScene, SecondScene, ThirdScene]),
            async (identity, ct) =>
            {
                var answered = await Register(client, identity, ct);
                await stopping.CancelAsync();
                return answered;
            },
            ct => Refresh(client, ct),
            stopping.Token);

        Assert.Equal(AddAllMissingRunOutcome.Cancelled, run.Outcome);
        Assert.Equal(1, run.Registered);
    }

    /// <summary>
    /// A cancelled run does not refresh the catalogue.
    /// </summary>
    /// <remarks>
    /// The refresh is what makes a completed registration set visible, and a run that stopped part
    /// way has no complete set to make visible.
    /// </remarks>
    [Fact]
    public async Task ACancelledRunIssuesNoCatalogueRefresh()
    {
        var client = Accepting();
        using var stopping = new CancellationTokenSource();

        await AddAllMissingPlanner.RunAsync(
            Identities([FirstScene, SecondScene]),
            async (identity, ct) =>
            {
                var answered = await Register(client, identity, ct);
                await stopping.CancelAsync();
                return answered;
            },
            ct => Refresh(client, ct),
            stopping.Token);

        Assert.DoesNotContain(
            nameof(IWhisparrMissingSceneActing.RefreshCatalogueAsync), client.Verbs);
    }

    /// <summary>
    /// Nothing the run answers grows with the identifier set.
    /// </summary>
    /// <remarks>
    /// Read off the record's own members rather than off one run's answer: a record carrying a list
    /// answers the same counts on a small entity and is unusable on a large one.
    /// </remarks>
    [Fact]
    public void TheRunRecordCarriesCountsAndNoCollection()
    {
        var members = typeof(AddAllMissingRun).GetProperties();

        Assert.All(
            members,
            member => Assert.True(
                member.PropertyType == typeof(int) || member.PropertyType.IsEnum,
                $"{member.Name} is a {member.PropertyType}, which can grow with the identifier set"));
        Assert.Equal(4, members.Length);
    }

    /// <summary>
    /// No request the run makes can grab, and none of them can retract.
    /// </summary>
    /// <remarks>
    /// The ordered verb log covers the whole outbound seam, so a grabbing verb reached by any path
    /// this run takes would appear in it. The retraction half is read off the shipped source, because
    /// a delete this run never happens to reach is still a delete this run could reach.
    /// </remarks>
    [Fact]
    public async Task NothingTheRunIssuesGrabsAndNothingInItRetracts()
    {
        var client = Accepting();

        await RunOver(client, [FirstScene, SecondScene]);

        Assert.NotEmpty(client.Verbs);
        Assert.DoesNotContain(nameof(IWhisparrSearchGrabbing.SearchMonitoredAsync), client.Verbs);
        Assert.All(
            new[] { "HttpMethod.Delete", "DeleteAsync", "\"DELETE\"", "Rename", "Organize" },
            retracting => Assert.DoesNotContain(
                retracting, PlannerSource(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The classification names the pin the measurement was transcribed into.
    /// </summary>
    /// <remarks>
    /// A rule read off the instance's own answer is an external fact, and the reader of this file has
    /// to be able to find what it was measured against.
    /// </remarks>
    [Fact]
    public void TheClassificationNamesThePinItRestsOn()
    {
        var source = PlannerSource();

        Assert.Contains(
            nameof(MonitorBodyPinTests.TheNewerGenerationNamesASceneItAlreadyHoldsByAnErrorCodeTheControlDoesNotCarry),
            source,
            StringComparison.Ordinal);
    }

    private static RecordingWhisparrClient Accepting()
        => new(RecordingWhisparrClient.Json(201, "{\"id\":31}"));

    private static Task<AddAllMissingRun> RunOver(
        RecordingWhisparrClient client, IReadOnlyList<string> identities)
        => AddAllMissingPlanner.RunAsync(
            Identities(identities),
            (identity, ct) => Register(client, identity, ct),
            ct => Refresh(client, ct),
            TestCt);

    private static async Task<WhisparrResponse?> Register(
        RecordingWhisparrClient client, string identity, CancellationToken ct)
        => await client.AddSceneAsync(Instance, StoredKey, identity, Defaults, ct);

    private static async Task Refresh(RecordingWhisparrClient client, CancellationToken ct)
        => await client.RefreshCatalogueAsync(
            Instance, StoredKey, WhisparrEntityKind.Studio, 31, ct);

    /// <summary>The identifiers, handed over one at a time as the library's own source hands them.</summary>
    /// <remarks>
    /// Genuinely asynchronous between items rather than a list dressed as one, so the run is driven
    /// through the same suspension points a database read would suspend at.
    /// </remarks>
    private static async IAsyncEnumerable<string> Identities(IReadOnlyList<string> identities)
    {
        foreach (var identity in identities)
        {
            await Task.Yield();
            yield return identity;
        }
    }

    // Found by walking up to the extension directory rather than by a counted-out "..": the test
    // assembly's depth below it varies with configuration and target framework.
    private static string PlannerSource()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "WhisparrSync", "Monitoring", "AddAllMissingPlanner.cs");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new InvalidOperationException(
            $"No src/WhisparrSync/Monitoring/AddAllMissingPlanner.cs above {AppContext.BaseDirectory}.");
    }
}
