using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Renamer.Contracts;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

public sealed class JobStatusEndpointTests
{
    private const string OwnScanJob = "ext:com.alextomas955.renamer:scan-library";
    private const string ForeignJob = "ext:com.example.other:its-own-work";

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

    private static RenamerJobStatus OkView(IResult result)
        => Assert.IsType<Ok<RenamerJobStatus>>(Unwrap(result)).Value!;

    // The caller these tests act as, and the owner the host would have stamped on a job they started.
    private static FakePrincipalAccessor Reader => FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

    private static JobOwner OwnerOfReader => JobOwner.FromPrincipal(Reader.Current)!;

    [Fact]
    public void ReadPermissionAndOwnJob_ReturnsTheRunsProgress()
    {
        var ext = RenamerFixture.CreateWithStore();
        var jobs = new RecordingJobService(Job("job-1", OwnScanJob, JobStatus.Running), OwnerOfReader);

        var view = OkView(ext.JobStatus("job-1", Reader, jobs));

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
        var ext = RenamerFixture.CreateWithStore();
        var jobs = new RecordingJobService(Job("job-2", ForeignJob, JobStatus.Running), OwnerOfReader);

        var result = ext.JobStatus("job-2", Reader, jobs);

        // not found rather than forbidden: answering "forbidden" confirms the id names a real job,
        // which is the fact the host's own gate withholds from this caller.
        Assert.IsType<NotFound>(Unwrap(result));
    }

    [Fact]
    public void UnknownJobId_IsNotFound()
    {
        var ext = RenamerFixture.CreateWithStore();
        var jobs = new RecordingJobService(job: null);

        Assert.IsType<NotFound>(Unwrap(ext.JobStatus("no-such-job", Reader, jobs)));
    }

    // A run's progress, sub-task text and error say what someone is doing with their own library, so
    // holding a job id is not enough to read one. The host records the principal that enqueued it and
    // answers anyone else with nothing.
    [Fact]
    public void AJobStartedByAnotherPrincipal_IsNotFound()
    {
        var ext = RenamerFixture.CreateWithStore();
        var jobs = new RecordingJobService(
            Job("job-4", OwnScanJob, JobStatus.Running), new JobOwner("user:999"));

        Assert.IsType<NotFound>(Unwrap(ext.JobStatus("job-4", Reader, jobs)));
    }

    // A job the host started for itself carries no owner. Nothing the panel polls is enqueued that
    // way, and serving one to a caller the host does name would be the hole this route closes.
    [Fact]
    public void AJobWithNoOwner_IsNotFoundToACallerTheHostNames()
    {
        var ext = RenamerFixture.CreateWithStore();
        var jobs = new RecordingJobService(Job("job-5", OwnScanJob, JobStatus.Running));

        Assert.IsType<NotFound>(Unwrap(ext.JobStatus("job-5", Reader, jobs)));
    }

    // With authentication disabled the host hands every request one bypass principal of kind System,
    // and JobOwner names no owner for it - so the job it enqueued has none either. Asking by owner
    // there would refuse the only user the instance has, which is the whole panel's job polling.
    [Fact]
    public void WithNoOwnerAtEitherEnd_TheRunIsStillReadable()
    {
        var ext = RenamerFixture.CreateWithStore();
        var jobs = new RecordingJobService(Job("job-6", OwnScanJob, JobStatus.Running));

        var bypass = new FakePrincipalAccessor();
        bypass.Set(new CovePrincipal
        {
            UserId = 1,
            Username = "owner",
            // The kind is what makes JobOwner decline, whatever user id sits beside it.
            Kind = PrincipalKind.System,
            Roles = new HashSet<string>(),
            Permissions = new HashSet<string> { Permissions.VideosRead },
        });
        Assert.Null(JobOwner.FromPrincipal(bypass.Current));

        Assert.Equal("job-6", OkView(ext.JobStatus("job-6", bypass, jobs)).Id);
    }

    [Theory]
    [InlineData(JobStatus.Pending, RenamerJobState.Pending)]
    [InlineData(JobStatus.Running, RenamerJobState.Running)]
    [InlineData(JobStatus.Completed, RenamerJobState.Completed)]
    [InlineData(JobStatus.Failed, RenamerJobState.Failed)]
    [InlineData(JobStatus.Cancelled, RenamerJobState.Cancelled)]
    public void EveryHostStatus_MapsToItsOwnWireState(JobStatus host, RenamerJobState expected)
    {
        var ext = RenamerFixture.CreateWithStore();
        var jobs = new RecordingJobService(Job("job-3", OwnScanJob, host), OwnerOfReader);

        Assert.Equal(expected, OkView(ext.JobStatus("job-3", Reader, jobs)).Status);
    }
}
