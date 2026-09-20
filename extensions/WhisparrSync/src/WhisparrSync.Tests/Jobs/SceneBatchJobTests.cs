using System.Globalization;
using System.Reflection;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Jobs;

// The decode cases are the maps the host can really hand a job. A decode that threw inside the
// runner would be a faulted job rather than an answer.
public sealed class SceneBatchJobTests
{
    private const string SceneId = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    // Whisparr answers a scene it holds no entry for with an empty array.
    private const string NoSceneRow = "[]";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public void ARunRoundTripsItsVerbAndItsSelection()
    {
        var decoded = SceneBatchJob.Decode(
            SceneBatchJob.Encode(SceneBatchVerb.Unmonitor, [7, 41]));

        Assert.Equal(SceneBatchVerb.Unmonitor, decoded.Verb);
        Assert.Equal([7, 41], decoded.CoveIds);
    }

    // A run nobody can read is a clean no-op rather than a throw.
    [Fact]
    public void ANullMapNamesNoVerbAndNoScene()
    {
        var decoded = SceneBatchJob.Decode(null);

        Assert.Null(decoded.Verb);
        Assert.Empty(decoded.CoveIds);
    }

    [Fact]
    public void AMapMissingTheIdKeyNamesNoScene()
    {
        var decoded = SceneBatchJob.Decode(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["verb"] = "monitor" });

        Assert.Equal(SceneBatchVerb.Monitor, decoded.Verb);
        Assert.Empty(decoded.CoveIds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("[\"one\"]")]
    public void AnIdListNothingCanBeReadOutOfNamesNoScene(string raw)
        => Assert.Empty(
            SceneBatchJob.Decode(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["verb"] = "monitor",
                    ["coveIds"] = raw,
                }).CoveIds);

    // A run that defaulted to the first enum member would add scenes on a map nobody could read.
    // The control at the end makes the four answers above about those maps, not about the reader.
    [Fact]
    public void AVerbThisProductDoesNotExpressNamesNoVerbAndNotTheFirstMember()
    {
        Assert.Null(VerbIn(null));
        Assert.Null(VerbIn(" "));
        Assert.Null(VerbIn("upgrade"));
        Assert.NotEqual(SceneBatchVerb.Add, VerbIn("upgrade"));
        Assert.Equal(SceneBatchVerb.Add, Enum.GetValues<SceneBatchVerb>()[0]);

        Assert.Equal(SceneBatchVerb.Exclude, VerbIn("exclude"));
    }

    // A member holding identifiers would grow with the selection. The declared members are read
    // rather than an instance, so a collection member added later fails here whatever a run put in
    // it.
    [Fact]
    public void TheRunRecordReportsThreeCountsAndListsNothing()
    {
        var members = typeof(SceneBatchJob).Assembly
            .GetType("WhisparrSync.Jobs.SceneBatchRun")!
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(member => member.Name != "EqualityContract")
            .ToList();

        Assert.Equal(3, members.Count(member => member.PropertyType == typeof(int)));
        Assert.Single(members, member => member.PropertyType.IsEnum);
        Assert.Equal(4, members.Count);
        Assert.DoesNotContain(
            members,
            member => member.PropertyType != typeof(int)
                && member.PropertyType.IsAssignableTo(typeof(System.Collections.IEnumerable)));
    }

    // A selection can genuinely carry one video twice, and acting twice issues two requests for it.
    // The order is asserted as well as the set, because a run reporting in another order cannot be
    // matched against the selection a user made.
    [Fact]
    public async Task TheRunActsOncePerSceneKeepingFirstAppearance()
    {
        var acted = new List<int>();

        var run = await RunOverAsync(
            [9, 4, 9, 7, 4],
            (coveId, _) =>
            {
                acted.Add(coveId);
                return Task.FromResult(SceneRefusalKind.None);
            },
            TestCt);

        Assert.Equal([9, 4, 7], acted);
        Assert.Equal(3, run.Applied);
    }

    [Fact]
    public async Task ASelectionNamingNoSceneDoesNoWorkAndOpensNoScope()
    {
        var reached = 0;

        var run = await RunOverAsync(
            [],
            (_, _) =>
            {
                reached++;
                return Task.FromResult(SceneRefusalKind.None);
            },
            TestCt);

        Assert.Equal(0, reached);
        Assert.Equal(SceneBatchJob.Untaken, run);
        Assert.Contains("nothing was done", SceneBatchJob.SummaryOf(run), StringComparison.OrdinalIgnoreCase);
    }

    // A background run carries no principal of its own, and Cove's per-principal query filters
    // answer an anonymous reader with zero rows and no error, which on this path would report every
    // scene as carrying no identity. The principal is read inside the run's own body, the only
    // place the elevation can be observed.
    [Fact]
    public async Task TheRunElevatesOneScopeForTheWholeRunToSystem()
    {
        var principals = new FakePrincipalAccessor();
        var services = new ServiceCollection()
            .AddSingleton<ICurrentPrincipalAccessor>(principals)
            .BuildServiceProvider();

        var seen = new List<PrincipalKind?>();
        var scopes = new List<IServiceProvider>();

        await SceneBatchJob.RunAsync(
            [1, 2, 3],
            services.GetRequiredService<IServiceScopeFactory>(),
            (IServiceProvider scoped, int _, CancellationToken _) =>
            {
                scopes.Add(scoped);
                seen.Add(scoped.GetRequiredService<ICurrentPrincipalAccessor>().Current?.Kind);
                return Task.FromResult(SceneRefusalKind.None);
            },
            new RecordingJobProgress(),
            TestCt);

        Assert.Equal([PrincipalKind.System, PrincipalKind.System, PrincipalKind.System], seen);
        Assert.Single(scopes.Distinct());

        // Restored after the run, so the elevation is the run's own span rather than the process's.
        Assert.Null(principals.Current);
    }

    // The host stops a job by cancelling its token, so classifying that as a failure would report a
    // shutdown as a fault.
    [Fact]
    public async Task ARunStoppedPartWayEndsAsCancelledAndKeepsWhatItApplied()
    {
        using var stopping = new CancellationTokenSource();
        var acted = new List<int>();

        var run = await RunOverAsync(
            [1, 2, 3, 4],
            (coveId, _) =>
            {
                acted.Add(coveId);
                if (coveId == 2)
                {
                    stopping.Cancel();
                }

                return Task.FromResult(SceneRefusalKind.None);
            },
            stopping.Token);

        // The stop is taken between two scenes, so the scene it was raised during finishes and the
        // two after it are never reached.
        Assert.Equal([1, 2], acted);
        Assert.Equal(SceneBatchRunOutcome.Cancelled, run.Outcome);
        Assert.Equal(2, run.Applied);
        Assert.Equal(0, run.Refused);
        Assert.Contains("then stopped", SceneBatchJob.SummaryOf(run), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASceneTheInstanceAlreadyHoldsIsCountedApartFromARefusal()
    {
        var answers = new Queue<SceneRefusalKind>(
        [
            SceneRefusalKind.None,
            SceneRefusalKind.WhisparrAlreadyHoldsThisScene,
            SceneRefusalKind.InstanceRefused,
        ]);

        var run = await RunOverAsync(
            [1, 2, 3], (_, _) => Task.FromResult(answers.Dequeue()), TestCt);

        Assert.Equal(1, run.Applied);
        Assert.Equal(1, run.AlreadyInThatState);
        Assert.Equal(1, run.Refused);
    }

    [Fact]
    public async Task TheRunsOneLineReportsCountsAndNamesNoScene()
    {
        var progress = new RecordingJobProgress();
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrClient.AddSceneAsync), MonitorHost.Json(200, "{}"));
        var coveId = await SeedSceneAsync(host, NoSceneRow);

        await EnqueueAsync(host, "add", coveId);
        await host.Jobs.RunLastAsync(progress, TestCt);

        // The whole line rather than a substring of it. A count's own digits can spell an id, so a
        // substring check cannot establish that no id is in the line, while an equality can.
        Assert.Equal("1 applied, 0 already so, 0 refused.", progress.Reports[^1].SubTask);
        Assert.DoesNotContain(SceneId, ReportedIn(progress), StringComparison.Ordinal);
    }

    // A search on a scene the instance holds no entry for would find nothing whatever the indexers
    // hold, so the batch keeps the single-scene route's pre-send refusal.
    [Fact]
    public async Task ASearchOverASceneTheInstanceDoesNotHoldCountsItAsRefusedAndSendsNothing()
    {
        var progress = new RecordingJobProgress();
        await using var host = await MonitorHost.CreateAsync();
        var coveId = await SeedSceneAsync(host, NoSceneRow);

        await EnqueueAsync(host, "search", coveId);
        await host.Jobs.RunLastAsync(progress, TestCt);

        Assert.Contains("0 applied", ReportedIn(progress), StringComparison.Ordinal);
        Assert.Contains("1 refused", ReportedIn(progress), StringComparison.Ordinal);
        Assert.Empty(host.Client.Acting);
        Assert.Equal(
            [
                new ReportedUnit(
                    coveId.ToString(CultureInfo.InvariantCulture),
                    JobUnitOutcome.Skipped,
                    nameof(SceneRefusalKind.WhisparrHasNoEntryForScene),
                    Disposed: true),
            ],
            progress.Units);
    }

    private static string ReportedIn(RecordingJobProgress progress)
        => string.Join('\n', progress.Reports.Select(report => report.SubTask));

    private static SceneBatchVerb? VerbIn(string? raw)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (raw is not null)
        {
            parameters["verb"] = raw;
        }

        return SceneBatchJob.Decode(parameters).Verb;
    }

    private static Task<SceneBatchRun> RunOverAsync(
        int[] coveIds,
        Func<int, CancellationToken, Task<SceneRefusalKind>> act,
        CancellationToken ct)
        => SceneBatchJob.RunAsync(
            coveIds,
            new ServiceCollection().BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>(),
            (_, coveId, sceneCt) => act(coveId, sceneCt),
            new RecordingJobProgress(),
            ct);

    private static async Task EnqueueAsync(MonitorHost host, string verb, int coveId)
    {
        var answered = await host.PostSceneBatchAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"entityType":"video","verb":"{{verb}}","coveIds":[{{coveId}}]}"""));
        answered.EnsureSuccessStatusCode();
    }

    private static async Task<int> SeedSceneAsync(MonitorHost host, string row)
    {
        host.Client.Answering(
            nameof(IWhisparrSceneStatusReading.ReadSceneByRemoteIdAsync),
            MonitorHost.Json(200, row));
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        return await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, SceneId);
    }
}
