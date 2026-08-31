using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace WhisparrSync.Tests;

/// <summary>
/// The half of the worker's lifecycle that needs no host: that it keeps running until its token is
/// cancelled, that cancelling it ends the task, and that the ending classifies as cancelled.
/// </summary>
/// <remarks>
/// The host stops the worker by cancelling the token and then blocking on the returned task, so a
/// worker that ignored the token would hang shutdown, disable and rebuild instead of failing. The
/// case that a returned-immediately worker could not pass is asserted first.
/// <para>
/// The instants are read through the host-configuration projection rather than off a field, so what
/// is asserted here is the same reading the containerized suite takes.
/// </para>
/// </remarks>
public sealed class BackgroundLifecycleTests
{
    /// <summary>How long the worker is left running before it is asked to be still running.</summary>
    /// <remarks>
    /// Far below the worker's own wake period, so a pass means it is waiting rather than that the
    /// window was too short to catch a wake.
    /// </remarks>
    private static readonly TimeSpan StillRunningWindow = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task TheWorkerKeepsRunningUntilItsTokenIsCancelled()
    {
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices();
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await Task.Delay(StillRunningWindow, TestContext.Current.CancellationToken);

        Assert.False(worker.IsCompleted, "the worker returned without being asked to stop");
        Assert.NotNull(ProbeOf(extension).WorkerStartedAtUtc);
        Assert.Null(ProbeOf(extension).WorkerCancelledAtUtc);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    /// <summary>
    /// A cancelled worker ends as cancelled rather than as faulted.
    /// </summary>
    /// <remarks>
    /// The distinction is the host's: its catch for the cancellation is conditioned on the token being
    /// cancelled, and everything else it logs as a fault and does not restart. A worker that swallowed
    /// the cancellation and returned normally would pass an assertion that only read the task as
    /// finished, so the status is asserted rather than the completion.
    /// </remarks>
    [Fact]
    public async Task ACancelledWorkerEndsAsCancelledRatherThanFaulted()
    {
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices();
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);

        Assert.True(worker.IsCanceled, "the worker did not end as cancelled");
        Assert.False(worker.IsFaulted);
    }

    /// <summary>
    /// The cancellation is recorded, at or after the start, and the start reading survives it.
    /// </summary>
    /// <remarks>
    /// Both instants together are what tells a stopped worker from one that never ran: a probe
    /// carrying neither is a worker the host never started at all.
    /// </remarks>
    [Fact]
    public async Task BothHalvesOfTheLifecycleAreReadableAfterTheStop()
    {
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices();
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);

        var probe = ProbeOf(extension);
        Assert.NotNull(probe.WorkerStartedAtUtc);
        Assert.NotNull(probe.WorkerCancelledAtUtc);
        Assert.True(
            probe.WorkerCancelledAtUtc >= probe.WorkerStartedAtUtc,
            $"the worker was recorded as cancelled at {probe.WorkerCancelledAtUtc:O}, before it started at {probe.WorkerStartedAtUtc:O}");
    }

    /// <summary>A worker that never ran reports neither instant.</summary>
    /// <remarks>
    /// The discriminating control for the three above: without it each of them could equally be
    /// reading a value that is set from construction.
    /// </remarks>
    [Fact]
    public void AnExtensionWhoseWorkerNeverRanReportsNeitherInstant()
    {
        var probe = ProbeOf(WhisparrSyncFixture.Create());

        Assert.Null(probe.WorkerStartedAtUtc);
        Assert.Null(probe.WorkerCancelledAtUtc);
    }

    /// <summary>The one service the worker resolves, registered as the extension registers it.</summary>
    private static ServiceProvider WorkerServices()
        => new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();

    private static HostConfigurationView ProbeOf(global::WhisparrSync.WhisparrSync extension)
        => ValueOf<HostConfigurationView>(
            extension.HostConfiguration(FakePrincipalAccessor.WithPermissions(Permissions.VideosRead)));

    private static T ValueOf<T>(IResult result)
        => Assert.IsType<T>(Assert.IsAssignableFrom<IValueHttpResult>(Unwrap(result)).Value);
}
