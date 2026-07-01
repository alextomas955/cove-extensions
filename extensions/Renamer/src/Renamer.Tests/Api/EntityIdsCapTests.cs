using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Api;

/// <summary>
/// The preview and renamer endpoints accept a caller-supplied id array, which is an unbounded fan-out:
/// preview runs the planner (DB hits) per id on the request thread, and renamer fans the same ids into
/// one job. Both reject an over-cap array with a 400 BEFORE any per-id work, so a runaway/oversized
/// request can't tie up a request thread or enqueue a giant job.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class EntityIdsCapTests
{
    // Keep in sync with Renamer.Api.cs MaxEntityIdsPerRequest. Over-cap = cap + 1.
    private const int Cap = 1000;

    /// <summary>Records every <c>Enqueue</c>; all other members are unused and throw.</summary>
    private sealed class RecordingJobService : IJobService
    {
        public List<(string type, string description)> Enqueued { get; } = [];

        public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
        {
            Enqueued.Add((type, description));
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
    public async Task PreviewAsync_OverCapIds_Returns400_AndMutatesNothing()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, _, fileId) = await ExecutorTestSeed.SeedVideoAsync(db, "/library/films", "raw.mkv", "Film");
            var (beforeName, beforePath) = await ExecutorTestSeed.ReadFileAsync(db, fileId);

            var ext = NewExtension();
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);
            var ids = Enumerable.Range(1, Cap + 1).ToArray(); // over the cap by one.

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", ids), db, principal, default);

            Assert.Equal(400, StatusOf(result));

            // The reject happens before any planner/DB work — the seeded row is untouched.
            var (afterName, afterPath) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal(beforeName, afterName);
            Assert.Equal(beforePath, afterPath);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public void RenamerEnqueue_OverCapIds_Returns400_AndDoesNotEnqueue()
    {
        var ext = NewExtension();
        var jobs = new RecordingJobService();
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);
        var ids = Enumerable.Range(1, Cap + 1).ToArray(); // over the cap by one.

        var result = ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest("video", ids), principal, jobs);

        Assert.Equal(400, StatusOf(result));
        Assert.Empty(jobs.Enqueued); // no work scheduled for an over-cap request.
    }

    [Fact]
    public void RenamerEnqueue_AtCapIds_PassesTheBound_AndEnqueues()
    {
        // Exactly at the cap is allowed — the bound rejects only what exceeds it.
        var ext = NewExtension();
        var jobs = new RecordingJobService();
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);
        var ids = Enumerable.Range(1, Cap).ToArray();

        var result = ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest("video", ids), principal, jobs);

        Assert.Equal(202, StatusOf(result));
        Assert.Single(jobs.Enqueued);
    }
}
