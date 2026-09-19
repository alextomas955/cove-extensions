using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

/// <summary>
/// The per-entity authorization the coarse route permission cannot supply: holding
/// <c>videos.write</c> does not grant write access to every video, so each path checks the entities
/// it is about to act on against the caller's own principal.
/// </summary>
public sealed class EntityAuthorizationTests
{
    /// <summary>Records every <c>Enqueue</c>; all other members are unused and throw.</summary>
    private sealed class RecordingJobService : IJobService
    {
        public List<string> Enqueued { get; } = [];

        public string Enqueue(string type, string description, Func<Cove.Core.Interfaces.IJobProgress, CancellationToken, Task> work, bool exclusive = true)
        {
            Enqueued.Add(type);
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
        ((IStatefulExtension)ext).SetStore(new FakeStore());
        return ext;
    }

    private static int StatusOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(Unwrap(result)).StatusCode ?? 0;

    [Fact]
    public async Task RenamerEnqueue_OneUnwritableId_Returns403_AndEnqueuesNothing()
    {
        var ext = NewExtension();
        var jobs = new RecordingJobService();
        var authz = new RecordingAuthorizationService();
        authz.Denied.Add((EntityKinds.Video, 8));

        var result = await ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest("video", [7, 8, 9]),
            FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite), jobs, authz, default);

        Assert.Equal(403, StatusOf(result));
        Assert.Empty(jobs.Enqueued);
    }

    [Fact]
    public async Task RenamerEnqueue_AsksTheWritePermission_ForEverySuppliedId()
    {
        var ext = NewExtension();
        var jobs = new RecordingJobService();
        var authz = new RecordingAuthorizationService();

        var result = await ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest("video", [7, 8, 9]),
            FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite), jobs, authz, default);

        Assert.Equal(202, StatusOf(result));
        Assert.Equal(
            [
                (Permissions.VideosWrite, EntityKinds.Video, 7),
                (Permissions.VideosWrite, EntityKinds.Video, 8),
                (Permissions.VideosWrite, EntityKinds.Video, 9),
            ],
            authz.Asked);
    }

    [Fact]
    public async Task RenamerEnqueue_CallerHoldingEveryPermission_AsksNothing()
    {
        var ext = NewExtension();
        var jobs = new RecordingJobService();
        var authz = new RecordingAuthorizationService();

        var result = await ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest("video", [7, 8, 9]),
            FakePrincipalAccessor.WithPermissions(Permissions.All), jobs, authz, default);

        Assert.Equal(202, StatusOf(result));
        Assert.Empty(authz.Asked);
    }
}
