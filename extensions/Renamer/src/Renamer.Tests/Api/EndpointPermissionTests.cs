using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

/// <summary>
/// The per-kind permission each handler checks once the route policy has admitted the caller. The
/// route admits a holder of any one kind's permission, so a request for another kind is refused here.
/// The authorized path enqueues exactly one exclusive renamer-batch job and returns 202 {jobId}.
/// </summary>
public sealed class EndpointPermissionTests
{
    /// <summary>Records every <c>Enqueue</c> call (including its exclusivity); all other members are unused and throw.</summary>
    private sealed class RecordingJobService : IJobService
    {
        public List<(string type, string description, bool exclusive)> Enqueued { get; } = [];

        public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
        {
            Enqueued.Add((type, description, exclusive));
            return "job-123";
        }

        public bool Cancel(string jobId) => throw new NotImplementedException();
        public bool ReorderQueued(string jobId, string? beforeJobId) => throw new NotImplementedException();
        public JobInfo? GetJob(string jobId) => throw new NotImplementedException();
        public IReadOnlyList<JobInfo> GetAllJobs() => throw new NotImplementedException();
        public IReadOnlyList<JobInfo> GetJobHistory() => throw new NotImplementedException();
    }

    private static global::Renamer.Renamer NewExtension()
    {
        var ext = RenamerFixture.Create();
        ((Cove.Plugins.IStatefulExtension)ext).SetStore(new FakeStore());
        return ext;
    }

    private static int StatusOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(Unwrap(result)).StatusCode ?? 0;

    [Fact]
    public async Task PreviewAsync_ImageRequest_RequiresImagesRead_NotVideosRead()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // A principal with only videos.read must be forbidden from previewing an image;
            // the matching images.read principal is allowed (200).
            var ext = NewExtension();
            var videoOnly = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);
            var denied = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("image", [1]), db, videoOnly, default);
            Assert.Equal(403, StatusOf(denied));

            // The matching images.read principal is not forbidden - the preview proceeds (a successful
            // preview returns a JSON value result with no explicit status code, i.e. 200, not 403).
            var imageOk = FakePrincipalAccessor.WithPermissions(Permissions.ImagesRead);
            var allowed = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("image", [1]), db, imageOk, default);
            Assert.NotEqual(403, StatusOf(allowed));
            Assert.IsAssignableFrom<IValueHttpResult>(Unwrap(allowed));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task RenamerEnqueue_WithVideosWrite_EnqueuesOneJob_AndReturns202WithJobId()
    {
        var ext = NewExtension();
        var jobs = new RecordingJobService();
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);

        var result = await ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest("video", [1, 2]), principal, jobs,
            new RecordingAuthorizationService(), default);

        Assert.Equal(202, StatusOf(result));
        // The 202 body carries the enqueued job id the fake returned.
        var body = Assert.IsType<global::Renamer.Contracts.JobEnqueued>(
            Assert.IsAssignableFrom<IValueHttpResult>(Unwrap(result)).Value);
        Assert.Equal("job-123", body.JobId);

        var (type, _, exclusive) = Assert.Single(jobs.Enqueued);
        Assert.Equal("ext:com.alextomas955.renamer:renamer-batch", type);
        // The destructive renamer job must enqueue exclusive so two batches cannot run at once.
        Assert.True(exclusive);
    }

    [Fact]
    public async Task RenamerEnqueue_ImageRequest_RequiresImagesWrite_NotVideosWrite()
    {
        var ext = NewExtension();
        var jobs = new RecordingJobService();

        // A principal holding only videos.write must not be able to enqueue an image renamer.
        var videoOnly = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);
        var denied = await ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest("image", [1]), videoOnly, jobs,
            new RecordingAuthorizationService(), default);
        Assert.Equal(403, StatusOf(denied));
        Assert.Empty(jobs.Enqueued);

        // The matching images.write principal succeeds.
        var imageOk = FakePrincipalAccessor.WithPermissions(Permissions.ImagesWrite);
        var allowed = await ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest("image", [1]), imageOk, jobs,
            new RecordingAuthorizationService(), default);
        Assert.Equal(202, StatusOf(allowed));
        var (_, _, exclusive) = Assert.Single(jobs.Enqueued);
        Assert.True(exclusive);
    }

    [Fact]
    public async Task RenamerEnqueue_AudioRequest_RequiresAudiosWrite()
    {
        var ext = NewExtension();
        var jobs = new RecordingJobService();

        // Audio is officially supported (kept in v1.6) and gated on audios.write - videos.write is denied.
        var videoOnly = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);
        Assert.Equal(403, StatusOf(await ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest("audio", [1]), videoOnly, jobs,
            new RecordingAuthorizationService(), default)));
        Assert.Empty(jobs.Enqueued);

        var audioOk = FakePrincipalAccessor.WithPermissions(Permissions.AudiosWrite);
        Assert.Equal(202, StatusOf(await ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest("audio", [1]), audioOk, jobs,
            new RecordingAuthorizationService(), default)));
        Assert.Single(jobs.Enqueued);
    }
}
