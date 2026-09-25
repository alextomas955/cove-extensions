using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Jobs;

public sealed class MonitoringBulkJobTests
{
    [Fact]
    public void EncodeThenDecodeRoundTripsTheTypeTheVerbTheScopeAndTheIds()
    {
        var decoded = MonitoringBulkJob.Decode(
            MonitoringBulkJob.Encode(
                "studios", MonitorBulkVerb.Monitor, MonitorScope.AllScenes, [7, 9]));

        Assert.Equal("studios", decoded.EntityType);
        Assert.Equal(MonitorBulkVerb.Monitor, decoded.Verb);
        Assert.Equal(MonitorScope.AllScenes, decoded.Scope);
        Assert.Equal([7, 9], decoded.EntityIds);
    }

    [Fact]
    public void AVerbTakingNoScopeRoundTripsWithNoScope()
    {
        var decoded = MonitoringBulkJob.Decode(
            MonitoringBulkJob.Encode("performers", MonitorBulkVerb.Unmonitor, null, [3]));

        Assert.Equal(MonitorBulkVerb.Unmonitor, decoded.Verb);
        Assert.Null(decoded.Scope);
    }

    // The host's parameter map holds strings only. A strict decode would throw on these shapes,
    // and the throw would land inside the host's job runner rather than in a handler.
    public static TheoryData<Dictionary<string, string>> UnreadableParameters()
        => new()
        {
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["entityType"] = "studios" },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["entityType"] = "studios",
                ["verb"] = "monitor",
                ["entityIds"] = "   ",
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["entityType"] = "studios",
                ["verb"] = "monitor",
                ["entityIds"] = "{not json",
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["entityType"] = "studios",
                ["verb"] = "monitor",
                ["entityIds"] = """{"ids":[1]}""",
            },
        };

    [Theory]
    [MemberData(nameof(UnreadableParameters))]
    public void AnUnreadableParameterMapDecodesToNoIdsAndNeverThrows(
        Dictionary<string, string> parameters)
    {
        var decoded = MonitoringBulkJob.Decode(parameters);

        Assert.Empty(decoded.EntityIds);
    }

    // The host declares the parameter map nullable, so null is its own case.
    [Fact]
    public void ANullParameterMapDecodesToNoIdsAndNeverThrows()
    {
        var decoded = MonitoringBulkJob.Decode(null);

        Assert.Empty(decoded.EntityIds);
        Assert.Null(decoded.Verb);
        Assert.Equal(string.Empty, decoded.EntityType);
    }

    [Fact]
    public void AnUnreadableVerbDecodesToNoVerbRatherThanToAnActingOne()
    {
        var decoded = MonitoringBulkJob.Decode(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["entityType"] = "studios",
            ["verb"] = "obliterate",
            ["entityIds"] = "[1]",
        });

        Assert.Null(decoded.Verb);
    }

    [Fact]
    public async Task ARepeatedIdIsActedOnOnceAndReportsOneOutcome()
    {
        var acted = new List<int>();

        var run = await RunAsync([4, 7, 4], (_, coveId, _) =>
        {
            acted.Add(coveId);
            return Task.FromResult(MonitorRefusalKind.None);
        });

        Assert.Equal([4, 7], acted);
        Assert.Equal([4, 7], run.Outcomes.Select(outcome => outcome.CoveId));
        Assert.Single(run.Outcomes, outcome => outcome.CoveId == 4);
    }

    [Fact]
    public async Task AnIdArrayOfNothingButRepeatsOfOneIdYieldsExactlyOneOutcome()
    {
        var acted = new List<int>();

        var run = await RunAsync([5, 5, 5, 5], (_, coveId, _) =>
        {
            acted.Add(coveId);
            return Task.FromResult(MonitorRefusalKind.None);
        });

        Assert.Equal([5], acted);
        Assert.Single(run.Outcomes);
    }

    // Two of the three share an outcome. A grouping bug is invisible when every outcome differs.
    [Fact]
    public async Task TheOutcomeListIsInTheSuppliedOrderEvenWhereTwoShareAnOutcome()
    {
        var refusals = new Dictionary<int, MonitorRefusalKind>
        {
            [11] = MonitorRefusalKind.NoIdentityInThisNamespace,
            [12] = MonitorRefusalKind.None,
            [13] = MonitorRefusalKind.NoIdentityInThisNamespace,
        };
        var progress = new RecordingJobProgress();

        var run = await RunAsync(
            [11, 12, 13], (_, coveId, _) => Task.FromResult(refusals[coveId]), progress);

        Assert.Equal([11, 12, 13], run.Outcomes.Select(outcome => outcome.CoveId));
        Assert.Equal(["11", "12", "13"], progress.Units.Select(unit => unit.UnitId));
    }

    [Fact]
    public async Task AnEmptySelectionActsOnNothingAndReportsNothingSelected()
    {
        var acted = 0;

        var run = await RunAsync([], (_, _, _) =>
        {
            acted++;
            return Task.FromResult(MonitorRefusalKind.None);
        });

        Assert.Equal(0, acted);
        Assert.Equal(MonitorBulkOutcomeKind.NothingSelected, run.Outcome);
        Assert.Empty(run.Outcomes);
    }

    [Fact]
    public async Task APreCancelledTokenClassifiesTheBatchAsCancelledAndActsOnNothing()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var acted = 0;

        var run = await RunAsync(
            [1, 2],
            (_, _, _) =>
            {
                acted++;
                return Task.FromResult(MonitorRefusalKind.None);
            },
            new RecordingJobProgress(),
            cancelled.Token);

        Assert.Equal(0, acted);
        Assert.Equal(MonitorBulkOutcomeKind.Cancelled, run.Outcome);
        Assert.Empty(run.Outcomes);
    }

    [Fact]
    public async Task ABatchCancelledPartWayKeepsTheOutcomesAlreadyRecorded()
    {
        using var cancelling = new CancellationTokenSource();

        var run = await RunAsync(
            [1, 2, 3],
            (_, coveId, _) =>
            {
                if (coveId == 2)
                {
                    cancelling.Cancel();
                }

                return Task.FromResult(MonitorRefusalKind.None);
            },
            new RecordingJobProgress(),
            cancelling.Token);

        Assert.Equal(MonitorBulkOutcomeKind.Cancelled, run.Outcome);
        Assert.Equal([1, 2], run.Outcomes.Select(outcome => outcome.CoveId));
    }

    // The batch carries no principal of its own, and Cove answers an anonymous reader with zero
    // rows and no error, which on this path reports every entity as carrying no identity.
    [Fact]
    public async Task TheBatchsWorkRunsUnderTheSystemPrincipal()
    {
        var seen = new List<PrincipalKind?>();

        await RunAsync([1, 2], (services, _, _) =>
        {
            seen.Add(services.GetRequiredService<ICurrentPrincipalAccessor>().Current?.Kind);
            return Task.FromResult(MonitorRefusalKind.None);
        });

        Assert.Equal([PrincipalKind.System, PrincipalKind.System], seen);
    }

    [Fact]
    public async Task ARefusalIsReportedAsItsOwnUnitOutcomeAndACleanOneAsSucceeded()
    {
        var progress = new RecordingJobProgress();
        var refusals = new Dictionary<int, MonitorRefusalKind>
        {
            [1] = MonitorRefusalKind.None,
            [2] = MonitorRefusalKind.NoIdentityInThisNamespace,
            [3] = MonitorRefusalKind.InstanceRefused,
        };

        await RunAsync([1, 2, 3], (_, coveId, _) => Task.FromResult(refusals[coveId]), progress);

        Assert.Equal(
            [JobUnitOutcome.Succeeded, JobUnitOutcome.Skipped, JobUnitOutcome.Failed],
            progress.Units.Select(unit => unit.Outcome));
    }

    private static Task<MonitorBulkRun> RunAsync(
        int[] entityIds,
        Func<IServiceProvider, int, CancellationToken, Task<MonitorRefusalKind>> act)
        => RunAsync(entityIds, act, new RecordingJobProgress());

    private static Task<MonitorBulkRun> RunAsync(
        int[] entityIds,
        Func<IServiceProvider, int, CancellationToken, Task<MonitorRefusalKind>> act,
        RecordingJobProgress progress)
        => RunAsync(entityIds, act, progress, TestContext.Current.CancellationToken);

    private static async Task<MonitorBulkRun> RunAsync(
        int[] entityIds,
        Func<IServiceProvider, int, CancellationToken, Task<MonitorRefusalKind>> act,
        RecordingJobProgress progress,
        CancellationToken ct)
    {
        var services = new ServiceCollection();
        services.AddScoped<ICurrentPrincipalAccessor>(_ => FakePrincipalAccessor.WithPermissions());
        await using var provider = services.BuildServiceProvider();

        return await MonitoringBulkJob.RunAsync(
            entityIds,
            provider.GetRequiredService<IServiceScopeFactory>(),
            act,
            progress,
            ct);
    }
}
