using System.Globalization;
using System.Reflection;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Jobs;

/// <summary>
/// What a scene batch carries across the host's parameter map, what it reports, the principal its
/// reads run under, and how it ends when the host stops it.
/// </summary>
/// <remarks>
/// The decode cases are the ones the host can really produce. It hands a job whatever map was stored
/// with it, and a decode that threw inside the runner would be a faulted job rather than an answer,
/// which is only reachable through that runner.
/// </remarks>
public sealed class SceneBatchJobTests
{
    /// <summary>A scene as the provider issues its identifier.</summary>
    private const string SceneId = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    /// <summary>A scene the instance holds no entry for.</summary>
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

    /// <summary>A map nothing can be read out of names no verb and no scene.</summary>
    /// <remarks>
    /// Each shape the host can hand over is driven on its own, so one covering case cannot stand for
    /// the rest. None throws: a run nobody can read is a clean no-op.
    /// </remarks>
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

    /// <summary>An id list nothing can be read out of names no scene.</summary>
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

    /// <summary>
    /// A verb this product does not express answers the absent value and never the first member.
    /// </summary>
    /// <remarks>
    /// A run that defaulted to the first member would add scenes on a map nobody could read. The
    /// control at the end is what makes the four answers above about those maps rather than about the
    /// reader.
    /// </remarks>
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

    /// <summary>The run record reports counts and lists nothing.</summary>
    /// <remarks>
    /// A member holding identifiers would grow with the selection. Read off the declared members
    /// rather than off an instance, so a collection member added later fails here whatever a run
    /// happened to put in it.
    /// </remarks>
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

    /// <summary>The selection is acted on once per scene, keeping each id's first appearance.</summary>
    /// <remarks>
    /// A selection can genuinely carry one video twice, and acting twice issues two requests for it.
    /// The order is asserted as well as the set, because a run reporting in another order cannot be
    /// matched against the selection a user made.
    /// </remarks>
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

    /// <summary>A selection naming no scene does no work and opens no scope.</summary>
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

    /// <summary>The run's own reads happen as System, inside one scope for the whole run.</summary>
    /// <remarks>
    /// A background run carries no principal of its own, and Cove's per-principal query filters
    /// answer an anonymous reader with zero rows and no error, which on this path would report every
    /// scene as carrying no identity. The principal is read inside the run's own body, which is the
    /// only place the elevation can be observed.
    /// </remarks>
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

    /// <summary>
    /// A run stopped part way ends as cancelled, and what it applied before that is still counted.
    /// </summary>
    /// <remarks>
    /// The host stops a job by cancelling its token, so classifying that as a failure would report a
    /// shutdown as a fault.
    /// </remarks>
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

    /// <summary>
    /// A scene the instance already holds is counted apart from one it would not take.
    /// </summary>
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

    /// <summary>The run's one line reports counts and names no scene.</summary>
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

    /// <summary>
    /// A search over a scene the instance does not hold counts that scene as refused and sends
    /// nothing.
    /// </summary>
    /// <remarks>
    /// The pre-send refusals the single-scene route takes are what a selection has to keep: a search
    /// on a scene the instance holds no entry for would find nothing whatever the indexers hold.
    /// </remarks>
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
                    nameof(SceneRefusalKind.WhisparrHasNoEntryForScene)),
            ],
            progress.Units);
    }

    private static string ReportedIn(RecordingJobProgress progress)
        => string.Join('\n', progress.Reports.Select(report => report.SubTask));

    /// <summary>The verb <paramref name="raw"/> decodes to, or the absent value.</summary>
    private static SceneBatchVerb? VerbIn(string? raw)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (raw is not null)
        {
            parameters["verb"] = raw;
        }

        return SceneBatchJob.Decode(parameters).Verb;
    }

    /// <summary>One run over <paramref name="coveIds"/>, with each scene's turn answered by
    /// <paramref name="act"/>.</summary>
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
