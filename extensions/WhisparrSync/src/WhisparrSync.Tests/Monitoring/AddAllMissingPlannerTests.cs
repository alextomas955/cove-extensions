using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// Whisparr v3 build 3.3.8.1097 answers an already-held scene and an unknown identifier with the
// same status and the same content type, so the classification reads the error code.
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

    // Written here rather than taken from the shared monitor host: that host owns a real Cove
    // context, and this file compiles where those types are absent.
    private const string StoredKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // The refresh comes last: a registration reaches the instance's catalogue only once one runs.
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

    // The refresh makes the instance re-read its metadata source for the whole entity, so it is
    // still worth sending when the run registered nothing.
    [Fact]
    public async Task ARunThatRegisteredNothingStillRefreshesTheCatalogue()
    {
        var client = new RecordingWhisparrClient(
            RecordingWhisparrClient.Json(400, ProbeFixtures.Read(AlreadyHeldFixture)));

        await RunOver(client, [FirstScene]);

        Assert.Contains(nameof(IWhisparrMissingSceneActing.RefreshCatalogueAsync), client.Verbs);
    }

    [Fact]
    public async Task AnIdentifierTheInstanceDoesNotRecogniseIsRefusedRatherThanHeld()
    {
        var client = new RecordingWhisparrClient(
            RecordingWhisparrClient.Json(400, ProbeFixtures.Read(UnknownIdentifierFixture)));

        var run = await RunOver(client, [FirstScene]);

        Assert.Equal(0, run.AlreadyHeld);
        Assert.Equal(1, run.Refused);
    }

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

    [Fact]
    public void AnUnreadableAnswerIsRefusedRatherThanHeld()
    {
        Assert.Equal(SceneRegistration.Refused, AddAllMissingPlanner.Classify(500, "not json"));
        Assert.Equal(SceneRegistration.Refused, AddAllMissingPlanner.Classify(400, null));
        Assert.Equal(SceneRegistration.Refused, AddAllMissingPlanner.Classify(409, "[]"));
    }

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

    // The bounded read answers a success status with an empty body and states the refusal on the
    // answer itself, so a classification reading only status and body would count it as registered.
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

    // A distinct outcome rather than a completed run: a job that did nothing still appears in the
    // host's Job Drawer, where a completed outcome reads as work that happened.
    [Fact]
    public async Task ARunOverNoIdentifierSendsNothingAndSaysSo()
    {
        var client = Accepting();

        var run = await RunOver(client, []);

        Assert.Empty(client.Verbs);
        Assert.Equal(AddAllMissingRunOutcome.NothingToRegister, run.Outcome);
        Assert.Equal(0, run.Registered);
    }

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

    // Read off the record's members rather than one run's answer: a record carrying a list answers
    // the same counts on a small entity and is unusable on a large one.
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

    // The retraction half reads the shipped source, because a delete this run never happens to
    // reach is still a delete this run could reach.
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

    [Fact]
    public void TheClassificationNamesThePinItRestsOn()
    {
        var source = PlannerSource();

        Assert.Contains(
            nameof(MonitorBodyPinTests.V3NamesASceneItAlreadyHoldsByAnErrorCodeTheControlDoesNotCarry),
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

    // Asynchronous between items rather than a list dressed as one, so the run is driven through
    // the same suspension points a database read would suspend at.
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
