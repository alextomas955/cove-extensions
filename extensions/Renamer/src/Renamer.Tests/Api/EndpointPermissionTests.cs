using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

public sealed class EndpointPermissionTests
{
    private static int StatusOf(IResult result) => Assert.IsType<IStatusCodeHttpResult>(Unwrap(result), exactMatch: false).StatusCode ?? 0;

    [Fact]
    public async Task PreviewAsync_ImageRequest_RequiresImagesRead_NotVideosRead()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // A principal with only videos.read must be forbidden from previewing an image;
            // the matching images.read principal is allowed (200).
            var ext = RenamerFixture.CreateWithStore();
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
            Assert.IsType<IValueHttpResult>(Unwrap(allowed), exactMatch: false);
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
        var ext = RenamerFixture.CreateWithStore();
        var jobs = new RecordingJobService();
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);

        var result = await ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest("video", [1, 2]), principal, jobs,
            new RecordingAuthorizationService(), default);

        Assert.Equal(202, StatusOf(result));
        // The 202 body carries the enqueued job id the fake returned.
        var body = Assert.IsType<global::Renamer.Contracts.JobEnqueued>(
            Assert.IsType<IValueHttpResult>(Unwrap(result), exactMatch: false).Value);
        Assert.Equal("job-123", body.JobId);

        var (type, _, exclusive) = Assert.Single(jobs.Enqueued);
        Assert.Equal("ext:com.alextomas955.renamer:renamer-batch", type);
        // The destructive renamer job must enqueue exclusive so two batches cannot run at once.
        Assert.True(exclusive);
    }

    [Fact]
    public async Task RenamerEnqueue_ImageRequest_RequiresImagesWrite_NotVideosWrite()
    {
        var ext = RenamerFixture.CreateWithStore();
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
        var ext = RenamerFixture.CreateWithStore();
        var jobs = new RecordingJobService();

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
