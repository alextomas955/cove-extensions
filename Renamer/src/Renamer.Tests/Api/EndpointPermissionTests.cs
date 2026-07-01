using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Api;

/// <summary>
/// Security-critical: the host's <c>[RequiresPermission]</c> filter is MVC-only and does
/// NOTHING for minimal-API extension endpoints, so each handler enforces the permission itself via
/// <see cref="ICurrentPrincipalAccessor"/>. These prove BOTH deny paths return 403 and — critically —
/// that the <c>/renamer</c> deny path does NOT enqueue a job. The authorized path enqueues exactly one
/// renamer-batch job and returns 202 {jobId}.
/// </summary>
[Trait("Tier", "Integration")]
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
        var ext = new global::Renamer.Renamer();
        ((Cove.Plugins.IStatefulExtension)ext).SetStore(new FakeStore());
        return ext;
    }

    private static int StatusOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    [Fact]
    public async Task PreviewAsync_WithoutVideosRead_Returns403_AndComputesNoPlan()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(db, "/library/films", "raw.mkv", "Denied Film");
            var ext = NewExtension();

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [videoId]), db, FakePrincipalAccessor.None(), default);

            Assert.Equal(403, StatusOf(result));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task PreviewAsync_ImageRequest_RequiresImagesRead_NotVideosRead()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // A principal with ONLY videos.read must be forbidden from previewing an IMAGE (F-02);
            // the matching images.read principal is allowed (200).
            var ext = NewExtension();
            var videoOnly = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);
            var denied = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("image", [1]), db, videoOnly, default);
            Assert.Equal(403, StatusOf(denied));

            // The matching images.read principal is NOT forbidden — the preview proceeds (a successful
            // preview returns a JSON value result with no explicit status code, i.e. 200, not 403).
            var imageOk = FakePrincipalAccessor.WithPermissions(Permissions.ImagesRead);
            var allowed = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("image", [1]), db, imageOk, default);
            Assert.NotEqual(403, StatusOf(allowed));
            Assert.IsAssignableFrom<IValueHttpResult>(allowed);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public void RenamerEnqueue_WithoutVideosWrite_Returns403_AndDoesNotEnqueue()
    {
        var ext = NewExtension();
        var jobs = new RecordingJobService();

        var result = ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest("video", [1, 2]), FakePrincipalAccessor.None(), jobs);

        Assert.Equal(403, StatusOf(result));
        Assert.Empty(jobs.Enqueued);
    }

    [Fact]
    public void RenamerEnqueue_WithVideosWrite_EnqueuesOneJob_AndReturns202WithJobId()
    {
        var ext = NewExtension();
        var jobs = new RecordingJobService();
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);

        var result = ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest("video", [1, 2]), principal, jobs);

        Assert.Equal(202, StatusOf(result));
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        Assert.NotNull(value);
        // The 202 body carries the enqueued jobId the fake returned.
        Assert.Equal("job-123", value!.GetType().GetProperty("jobId")!.GetValue(value));

        var (type, _, exclusive) = Assert.Single(jobs.Enqueued);
        Assert.Equal("ext:com.alextomas955.renamer:renamer-batch", type);
        // F-04: the destructive renamer job must enqueue EXCLUSIVE so two batches cannot run at once.
        Assert.True(exclusive);
    }

    [Fact]
    public void RenamerEnqueue_ImageRequest_RequiresImagesWrite_NotVideosWrite()
    {
        var ext = NewExtension();
        var jobs = new RecordingJobService();

        // A principal holding ONLY videos.write must NOT be able to enqueue an IMAGE renamer (F-02).
        var videoOnly = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);
        var denied = ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest("image", [1]), videoOnly, jobs);
        Assert.Equal(403, StatusOf(denied));
        Assert.Empty(jobs.Enqueued);

        // The matching images.write principal succeeds.
        var imageOk = FakePrincipalAccessor.WithPermissions(Permissions.ImagesWrite);
        var allowed = ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest("image", [1]), imageOk, jobs);
        Assert.Equal(202, StatusOf(allowed));
        var (_, _, exclusive) = Assert.Single(jobs.Enqueued);
        Assert.True(exclusive);
    }

    [Fact]
    public void RenamerEnqueue_AudioRequest_RequiresAudiosWrite()
    {
        var ext = NewExtension();
        var jobs = new RecordingJobService();

        // Audio is officially supported (kept in v1.6) and gated on audios.write — videos.write is denied.
        var videoOnly = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);
        Assert.Equal(403, StatusOf(ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest("audio", [1]), videoOnly, jobs)));
        Assert.Empty(jobs.Enqueued);

        var audioOk = FakePrincipalAccessor.WithPermissions(Permissions.AudiosWrite);
        Assert.Equal(202, StatusOf(ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest("audio", [1]), audioOk, jobs)));
        Assert.Single(jobs.Enqueued);
    }

    [Fact]
    public async Task UndoAsync_WithoutVideosWrite_Returns403_BeforeAnyDiskOrDbTouch()
    {
        // No scope factory / event bus is wired: UndoAsync must return 403 from the FIRST permission
        // check, before it ever opens a scope or reads the RevertLog. If it touched the
        // scope factory it would NRE here — the absence of a throw proves the 403-first ordering.
        var ext = NewExtension();

        var result = await ext.UndoAsync(FakePrincipalAccessor.None(), default);

        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task LastBatchAsync_WithoutVideosRead_Returns403()
    {
        var ext = NewExtension();

        var result = await ext.LastBatchAsync(FakePrincipalAccessor.None(), default);

        Assert.Equal(403, StatusOf(result));
    }
}
