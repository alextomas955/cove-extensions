using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Renamer.Options;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

[Collection(CoveDataExtensionScope.CollectionName)]
public sealed class EntityAuthorizationTests
{
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

    private static async Task<(global::Renamer.Renamer Ext, FakeStore Store)> LoadedExtensionAsync(
        LibraryDatabase library)
    {
        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(
            new RenamerOptions { FilenameTemplate = "$title", SameVolumeConcurrency = 1 });
        var ext = RenamerFixture.Create();
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(library.BuildProvider());
        return (ext, store);
    }

    // Seeds two single-file videos in one folder with real bytes, and returns their entity ids.
    private static async Task<(int First, int Second)> SeedTwoVideosAsync(LibraryDatabase library, TempDir dir)
    {
        string folderPath = dir.Root.Replace('\\', '/');
        await using var seed = library.NewContext();
        var (folderId, firstId, _) = await ExecutorTestSeed.SeedVideoAsync(seed, folderPath, "raw 0.mkv", "Film 0");

        var second = new Video { Title = "Film 1", Organized = true };
        seed.Set<Video>().Add(second);
        await seed.SaveChangesAsync();
        await ExecutorTestSeed.SeedAdditionalFileAsync(seed, folderId, second.Id, "raw 1.mkv");

        File.WriteAllText(Path.Combine(dir.Root, "raw 0.mkv"), "bytes-0");
        File.WriteAllText(Path.Combine(dir.Root, "raw 1.mkv"), "bytes-1");
        return (firstId, second.Id);
    }

    private static CovePrincipal Caller(params string[] permissions)
        => FakePrincipalAccessor.WithPermissions(permissions).Current!;

    // Reads the stored scan aggregate with the wire's camelCase and string enums.
    private static readonly JsonSerializerOptions EnumJson =
        new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    [Fact]
    public async Task RunRenamerLibraryJobAsync_DeniedEntity_LeavesItsFileUntouched_ButRenamesTheAllowedOne()
    {
        using var dir = new TempDir();
        await using var library = await LibraryDatabase.CreateAsync();
        var (_, secondId) = await SeedTwoVideosAsync(library, dir);
        library.Authorization.Denied.Add((EntityKinds.Video, secondId));

        var (ext, _) = await LoadedExtensionAsync(library);

        await ext.RunRenamerLibraryJobAsync(
            Caller(Permissions.VideosWrite), [RenamerFileKind.Video], new FakeJobProgress(), default);

        Assert.True(File.Exists(Path.Combine(dir.Root, "Film 0.mkv")));
        Assert.True(File.Exists(Path.Combine(dir.Root, "raw 1.mkv")), "the denied entity must not move");
        Assert.False(File.Exists(Path.Combine(dir.Root, "Film 1.mkv")));
    }

    [Fact]
    public async Task RunRenamerLibraryJobAsync_AsksTheWritePermission_AgainstTheCallersOwnPrincipal()
    {
        using var dir = new TempDir();
        await using var library = await LibraryDatabase.CreateAsync();
        await SeedTwoVideosAsync(library, dir);

        var (ext, _) = await LoadedExtensionAsync(library);
        var caller = Caller(Permissions.VideosWrite);

        await ext.RunRenamerLibraryJobAsync(
            caller, [RenamerFileKind.Video], new FakeJobProgress(), default);

        Assert.NotEmpty(library.Authorization.Asked);
        Assert.All(
            library.Authorization.Asked,
            ask => Assert.Equal(Permissions.VideosWrite, ask.Permission));

        // The snapshot the enqueue took, never the System principal the surrounding elevation installs.
        Assert.Equal(PrincipalKind.User, library.Authorization.LastPrincipal?.Kind);
        Assert.Equal(caller.Username, library.Authorization.LastPrincipal?.Username);
    }

    [Fact]
    public async Task RunRenamerLibraryJobAsync_CallerHoldingEveryPermission_AsksNothing()
    {
        using var dir = new TempDir();
        await using var library = await LibraryDatabase.CreateAsync();
        await SeedTwoVideosAsync(library, dir);

        var (ext, _) = await LoadedExtensionAsync(library);

        await ext.RunRenamerLibraryJobAsync(
            Caller(Permissions.All), [RenamerFileKind.Video], new FakeJobProgress(), default);

        Assert.True(File.Exists(Path.Combine(dir.Root, "Film 0.mkv")));
        Assert.True(File.Exists(Path.Combine(dir.Root, "Film 1.mkv")));
        Assert.Empty(library.Authorization.Asked);
    }

    [Fact]
    public async Task RunScanLibraryJobAsync_DeniedEntity_IsNotAggregated()
    {
        using var dir = new TempDir();
        await using var library = await LibraryDatabase.CreateAsync();
        var (_, secondId) = await SeedTwoVideosAsync(library, dir);
        library.Authorization.Denied.Add((EntityKinds.Video, secondId));

        var (ext, store) = await LoadedExtensionAsync(library);

        await ext.RunScanLibraryJobAsync(
            Caller(Permissions.VideosRead), [RenamerFileKind.Video], null, new FakeJobProgress(), default);

        var json = await store.GetAsync(global::Renamer.Renamer.LastScanSummaryKey);
        var summary = JsonSerializer.Deserialize<global::Renamer.Contracts.ScanSummary>(json!, EnumJson)!;

        var kind = Assert.Single(summary.Kinds);
        Assert.Equal(1, kind.Entities);

        Assert.NotEmpty(library.Authorization.Asked);
        Assert.All(
            library.Authorization.Asked,
            ask => Assert.Equal(Permissions.VideosRead, ask.Permission));
    }
}
