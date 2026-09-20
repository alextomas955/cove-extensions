using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Library;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Jobs;

// The job-status projection copies the host's Summary straight onto the wire. A run whose last
// progress call is not its own summary therefore publishes the host's sentence, which a test
// asserting only on the run's own string would not see.
public sealed class SyncLibraryJobTests
{
    private const string AcceptedFixture = "whisparr-v3-3.3.8.1097-scene-add-accepted.json";

    // The host writes this sentence into the job summary of every unit-mode job once the last unit
    // completes. Transcribed from the host rather than composed, so the phrasing a reader sees is
    // what is asserted.
    private const string HostsOwnUnitSentence = "5,898 of 5,898 units succeeded";

    private static readonly string[] ForbiddenFragments =
        ["batch", "chunk", "unit", "slice", "succeeded"];

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public void TheMonitorChoiceSurvivesTheHostsStringOnlyMap()
    {
        Assert.True(SyncLibraryJob.Decode(SyncLibraryJob.Encode(true)).AlsoMonitor);
        Assert.False(SyncLibraryJob.Decode(SyncLibraryJob.Encode(false)).AlsoMonitor);
    }

    // A default of true would set flags across a whole library for a run that named nothing. The
    // map is read inside the host's job runner, where a throw is a faulted job.
    [Fact]
    public void AMapNothingCanBeReadOutOfMonitorsNothing()
    {
        Assert.False(SyncLibraryJob.Decode(null).AlsoMonitor);
        Assert.False(SyncLibraryJob.Decode(new Dictionary<string, string>()).AlsoMonitor);
        Assert.False(
            SyncLibraryJob.Decode(new Dictionary<string, string> { ["alsoMonitor"] = "yes" })
                .AlsoMonitor);
    }

    [Fact]
    public async Task TheRunOffersEveryIdentifierTheLibraryStreamYields()
    {
        var offered = new List<string>();
        var progress = new RecordingJobProgress();

        var run = await RunAsync(
            new SyncLibraryBatch(AlsoMonitor: false),
            Identifiers(4),
            progress,
            (identity, _) =>
            {
                offered.Add(identity);
                return Task.FromResult<WhisparrResponse?>(Accepted);
            });

        Assert.Equal(SyncLibraryRunOutcome.Completed, run.Outcome);
        Assert.Equal(4, run.Registered);
        Assert.Equal([4], progress.DeclaredUnitCounts);
        Assert.Equal(4, offered.Count);
        Assert.Equal(4, progress.Units.Count);
    }

    // A run that declared zero units never derives a fraction and would sit in the job list with
    // no ending, so it declares no count at all.
    [Fact]
    public async Task ARunThatCouldNotBeAimedOffersNothingAndSaysSo()
    {
        var progress = new RecordingJobProgress();
        await using var provider = Scopes(StubLibraryIdentities.OfScenes(Identifiers(3)));

        var run = await SyncLibraryJob.RunAsync(
            new SyncLibraryBatch(AlsoMonitor: false),
            provider.GetRequiredService<IServiceScopeFactory>(),
            (_, _, _) => Task.FromResult<SyncLibraryAiming?>(null),
            progress,
            TestCt);

        Assert.Equal(SyncLibraryRunOutcome.NothingToRegister, run.Outcome);
        Assert.Equal(0, run.Offered);
        Assert.Empty(progress.Units);
        Assert.Empty(progress.DeclaredUnitCounts);
        Assert.Equal(SyncLibraryJob.NoInstanceLine, Assert.Single(progress.Summaries));
    }

    // The projection fills both the summary and the sub-task from the host's job, and the host
    // copies its summary over the sub-task, so both are asserted.
    [Fact]
    public async Task WhatTheWireCarriesAboutTheRunCountsScenesAndNothingElse()
    {
        var progress = new RecordingJobProgress();

        await RunAsync(
            new SyncLibraryBatch(AlsoMonitor: false),
            Identifiers(3),
            progress,
            (_, _) => Task.FromResult<WhisparrResponse?>(Accepted));

        var ending = Assert.Single(progress.Summaries);
        var reported = BulkJobStatus.From(Finished(ending));

        foreach (var forbidden in ForbiddenFragments)
        {
            Assert.DoesNotContain(forbidden, reported.Summary!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, reported.SubTask!, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("scenes registered", reported.Summary!, StringComparison.Ordinal);
    }

    // The projection copies whatever the host holds, so nothing downstream filters the phrasing
    // out. This is why a run's last progress call has to be its own summary.
    [Fact]
    public void TheHostsOwnUnitSentenceReachesTheWireWhereARunLetsItStand()
    {
        var reported = BulkJobStatus.From(Finished(HostsOwnUnitSentence));

        Assert.Equal(HostsOwnUnitSentence, reported.Summary);
        Assert.Equal(HostsOwnUnitSentence, reported.SubTask);
    }

    private static WhisparrResponse Accepted
        => RecordingWhisparrClient.Json(201, ProbeFixtures.Read(AcceptedFixture));

    // The sub-task carries the summary because the host copies one over the other when it
    // finalizes successful work.
    private static JobInfo Finished(string ending)
        => new(
            "job-1",
            "ext:com.alextomas955.whisparrsync:sync-library",
            "a library run",
            JobStatus.Completed,
            1d,
            ending,
            DateTime.UnixEpoch,
            DateTime.UnixEpoch,
            null,
            UnitsTotal: 3,
            UnitsCompleted: 3,
            UnitsSucceeded: 3,
            UnitsFailed: 0,
            UnitsSkipped: 0,
            Summary: ending);

    private static async Task<SyncLibraryRun> RunAsync(
        SyncLibraryBatch batch,
        IReadOnlyList<string> identifiers,
        RecordingJobProgress progress,
        Func<string, CancellationToken, Task<WhisparrResponse?>> register)
    {
        await using var provider = Scopes(StubLibraryIdentities.OfScenes(identifiers));

        return await SyncLibraryJob.RunAsync(
            batch,
            provider.GetRequiredService<IServiceScopeFactory>(),
            (_, _, _) => Task.FromResult<SyncLibraryAiming?>(
                new SyncLibraryAiming(
                    WhisparrGeneration.V3,
                    SyncRegisters.Scenes,
                    (identity, ct) => Offered(register, identity, ct),
                    RegisterSite: null,
                    Monitor: null)),
            progress,
            TestCt);
    }

    private static ServiceProvider Scopes(ILibrarySceneIdentityPort identities)
        => new ServiceCollection().AddSingleton(identities).BuildServiceProvider();

    private static List<string> Identifiers(int count)
        => [.. Enumerable.Range(1, count).Select(n => $"{n:x8}-0000-4000-8000-000000000000")];

    private static async Task<SyncRegistration> Offered(
        Func<string, CancellationToken, Task<WhisparrResponse?>> register,
        string identity,
        CancellationToken ct)
        => SyncRegistration.Offered(await register(identity, ct));
}
