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

    [Theory]
    [InlineData("image", Permissions.VideosWrite, Permissions.ImagesWrite)]
    [InlineData("audio", Permissions.VideosWrite, Permissions.AudiosWrite)]
    [InlineData("text", Permissions.VideosWrite, Permissions.TextsWrite)]
    [InlineData("video", Permissions.ImagesWrite, Permissions.VideosWrite)]
    public async Task RenamerEnqueue_RequiresTheRequestKindsWritePermission(
        string entityType, string otherKindsWrite, string ownWrite)
    {
        var ext = RenamerFixture.CreateWithStore();
        var jobs = new RecordingJobService();

        var denied = await ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest(entityType, [1]),
            FakePrincipalAccessor.WithPermissions(otherKindsWrite), jobs,
            new RecordingAuthorizationService(), default);
        Assert.Equal(403, StatusOf(denied));
        Assert.Empty(jobs.Enqueued);

        var allowed = await ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest(entityType, [1]),
            FakePrincipalAccessor.WithPermissions(ownWrite), jobs,
            new RecordingAuthorizationService(), default);
        Assert.Equal(202, StatusOf(allowed));
        var (_, _, exclusive) = Assert.Single(jobs.Enqueued);
        Assert.True(exclusive);
    }

    [Fact]
    public async Task LastBatchAsync_AdmitsACallerWhoCanReadOnlyTexts()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, new global::Renamer.Options.RenamerOptions());

            var result = await ext.LastBatchAsync(FakePrincipalAccessor.WithPermissions(Permissions.TextsRead), default);

            Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<global::Renamer.Contracts.LastBatchSummary>>(result.Result);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
