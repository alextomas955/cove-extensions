using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Library;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Jobs;

/// <summary>
/// What one enqueued library run carries, what it offers, and what the wire then reports about it.
/// </summary>
/// <remarks>
/// The wire half is here rather than beside the run, because this extension's own job-status
/// projection copies the host's <c>Summary</c> straight onto the wire. A run whose last progress call
/// is not its own summary therefore publishes the host's sentence, and a test asserting only on the
/// run's own string would not see it.
/// </remarks>
public sealed class SyncLibraryJobTests
{
    private const string AcceptedFixture = "whisparr-v3-3.3.8.1097-scene-add-accepted.json";

    /// <summary>The host's own aggregate sentence, transcribed from its implementation.</summary>
    /// <remarks>
    /// Written down rather than computed, because what matters is the phrasing a reader would see.
    /// The host writes it into the job's summary on every unit-mode job once the last unit completes.
    /// </remarks>
    private const string HostsOwnUnitSentence = "5,898 of 5,898 units succeeded";

    private static readonly string[] ForbiddenFragments =
        ["batch", "chunk", "unit", "slice", "succeeded"];

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>The monitor choice survives the host's string-only parameter map.</summary>
    [Fact]
    public void TheMonitorChoiceSurvivesTheHostsStringOnlyMap()
    {
        Assert.True(SyncLibraryJob.Decode(SyncLibraryJob.Encode(true)).AlsoMonitor);
        Assert.False(SyncLibraryJob.Decode(SyncLibraryJob.Encode(false)).AlsoMonitor);
    }

    /// <summary>A map nothing can be read out of monitors nothing.</summary>
    /// <remarks>
    /// Read inside the host's job runner, where a throw is a faulted job rather than a handled
    /// answer. A default of true would set flags on a reader's whole library for a run that named
    /// nothing.
    /// </remarks>
    [Fact]
    public void AMapNothingCanBeReadOutOfMonitorsNothing()
    {
        Assert.False(SyncLibraryJob.Decode(null).AlsoMonitor);
        Assert.False(SyncLibraryJob.Decode(new Dictionary<string, string>()).AlsoMonitor);
        Assert.False(
            SyncLibraryJob.Decode(new Dictionary<string, string> { ["alsoMonitor"] = "yes" })
                .AlsoMonitor);
    }

    /// <summary>
    /// The run offers every identifier the library stream yields, and declares that many.
    /// </summary>
    /// <remarks>
    /// The count and the offers come from one member called twice, so the declared total cannot
    /// disagree with the number of ticks.
    /// </remarks>
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

    /// <summary>A run that could not be aimed at an instance offers nothing and says so.</summary>
    /// <remarks>
    /// Its own sentence, because a run that reached no instance is a different fact from a library
    /// carrying no identifier. It declares no count either: a run declaring zero never derives a
    /// fraction and would sit in the job list with no ending.
    /// </remarks>
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

    /// <summary>What the wire carries about the run counts scenes and nothing else.</summary>
    /// <remarks>
    /// The projection fills both the summary and the sub-task from the host's job, and the host copies
    /// its summary over the sub-task, so both are asserted.
    /// </remarks>
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

    /// <summary>
    /// The host's own sentence reaches the wire unaltered where a run lets it stand.
    /// </summary>
    /// <remarks>
    /// Why the run's last progress call has to be its own summary. The projection is a conduit: it
    /// copies whatever the host holds, so nothing downstream can filter the phrasing out, and a run
    /// that ends with a report leaves the host's sentence in place.
    /// </remarks>
    [Fact]
    public void TheHostsOwnUnitSentenceReachesTheWireWhereARunLetsItStand()
    {
        var reported = BulkJobStatus.From(Finished(HostsOwnUnitSentence));

        Assert.Equal(HostsOwnUnitSentence, reported.Summary);
        Assert.Equal(HostsOwnUnitSentence, reported.SubTask);
    }

    private static WhisparrResponse Accepted
        => RecordingWhisparrClient.Json(201, ProbeFixtures.Read(AcceptedFixture));

    /// <summary>A finished unit-mode job whose summary and sub-task read <paramref name="ending"/>.</summary>
    /// <remarks>
    /// The sub-task carries the summary because the host copies one over the other when it finalizes
    /// successful work.
    /// </remarks>
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

    /// <summary>One offer classified the way the scene pass classifies it.</summary>
    private static async Task<SyncRegistration> Offered(
        Func<string, CancellationToken, Task<WhisparrResponse?>> register,
        string identity,
        CancellationToken ct)
        => SyncRegistration.Offered(await register(identity, ct));
}
