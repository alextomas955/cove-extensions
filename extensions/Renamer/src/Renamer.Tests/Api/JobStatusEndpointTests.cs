using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Renamer.Contracts;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

/// <summary>
/// <c>JobStatus</c> serves one of this extension's own runs, and nothing else.
/// </summary>
/// <remarks>
/// The route exists because Cove gates its own job endpoint on unrestricted read, so a scoped account
/// cannot watch a run it started itself. That makes the confinement the point of these tests rather
/// than a detail: a route answering for any job id would be a way around the host's gate instead of a
/// replacement for the part of it this extension owns.
/// <para>
/// The job types are spelled as literal strings rather than read from the extension, so a change to
/// the prefix it mints has to be made here too instead of being agreed with automatically.
/// </para>
/// </remarks>
public sealed class JobStatusEndpointTests
{
    private const string OwnScanJob = "ext:com.alextomas955.renamer:scan-library";
    private const string ForeignJob = "ext:com.example.other:its-own-work";

    /// <summary>Answers for the one job handed to the constructor; every other member throws.</summary>
    private sealed class StubJobService(JobInfo? job) : IJobService
    {
        public JobInfo? GetJob(string jobId) => job is not null && job.Id == jobId ? job : null;

        public string Enqueue(string type, string description, Func<Cove.Core.Interfaces.IJobProgress, CancellationToken, Task> work, bool exclusive = true)
            => throw new NotImplementedException();
        public bool Cancel(string jobId) => throw new NotImplementedException();
        public bool ReorderQueued(string jobId, string? beforeJobId) => throw new NotImplementedException();
        public IReadOnlyList<JobInfo> GetAllJobs() => throw new NotImplementedException();
        public IReadOnlyList<JobInfo> GetJobHistory() => throw new NotImplementedException();
    }

    private static JobInfo Job(string id, string type, JobStatus status, double progress = 0.5) => new(
        Id: id,
        Type: type,
        Description: "[Renamer] Scan library",
        Status: status,
        Progress: progress,
        SubTask: "Scanning library… 3/9",
        StartedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        CompletedAt: null,
        Error: null,
        EtaSeconds: 12.5);

    private static global::Renamer.Renamer NewExtension()
    {
        var ext = RenamerFixture.Create();
        ((IStatefulExtension)ext).SetStore(new FakeStore());
        return ext;
    }

    private static RenamerJobStatus OkView(IResult result)
        => Assert.IsType<Ok<RenamerJobStatus>>(Unwrap(result)).Value!;

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(Unwrap(result)).StatusCode ?? 0;

    [Fact]
    public void ReadPermissionAndOwnJob_ReturnsTheRunsProgress()
    {
        var ext = NewExtension();
        var jobs = new StubJobService(Job("job-1", OwnScanJob, JobStatus.Running));

        var view = OkView(ext.JobStatus(
            "job-1", FakePrincipalAccessor.WithPermissions(Permissions.VideosRead), jobs));

        Assert.Equal("job-1", view.Id);
        Assert.Equal(RenamerJobState.Running, view.Status);
        Assert.Equal(0.5, view.Progress);
        Assert.Equal("Scanning library… 3/9", view.SubTask);
        Assert.Null(view.Error);
        Assert.Equal(12.5, view.EtaSeconds);
    }

    [Fact]
    public void JobOwnedByAnotherExtension_IsNotFound_NotForbidden()
    {
        var ext = NewExtension();
        var jobs = new StubJobService(Job("job-2", ForeignJob, JobStatus.Running));

        var result = ext.JobStatus(
            "job-2", FakePrincipalAccessor.WithPermissions(Permissions.VideosRead), jobs);

        // NOT FOUND rather than FORBIDDEN: answering "forbidden" confirms the id names a real job,
        // which is the fact the host's own gate withholds from this caller.
        Assert.IsType<NotFound>(Unwrap(result));
    }

    [Fact]
    public void UnknownJobId_IsNotFound()
    {
        var ext = NewExtension();
        var jobs = new StubJobService(job: null);

        Assert.IsType<NotFound>(Unwrap(ext.JobStatus(
            "no-such-job", FakePrincipalAccessor.WithPermissions(Permissions.VideosRead), jobs)));
    }

    [Fact]
    public void NoReadPermission_IsForbidden()
    {
        var ext = NewExtension();
        var jobs = new StubJobService(Job("job-1", OwnScanJob, JobStatus.Running));

        // The job exists and belongs to this extension, so a 403 here can only come from the
        // permission gate rather than from the confinement check below it.
        Assert.Equal(
            StatusCodes.Status403Forbidden,
            StatusOf(ext.JobStatus("job-1", FakePrincipalAccessor.None(), jobs)));
    }

    [Theory]
    [InlineData(JobStatus.Pending, RenamerJobState.Pending)]
    [InlineData(JobStatus.Running, RenamerJobState.Running)]
    [InlineData(JobStatus.Completed, RenamerJobState.Completed)]
    [InlineData(JobStatus.Failed, RenamerJobState.Failed)]
    [InlineData(JobStatus.Cancelled, RenamerJobState.Cancelled)]
    public void EveryHostStatus_MapsToItsOwnWireState(JobStatus host, RenamerJobState expected)
    {
        var ext = NewExtension();
        var jobs = new StubJobService(Job("job-3", OwnScanJob, host));

        Assert.Equal(expected, OkView(ext.JobStatus(
            "job-3", FakePrincipalAccessor.WithPermissions(Permissions.VideosRead), jobs)).Status);
    }
}
