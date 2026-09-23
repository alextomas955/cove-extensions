using Cove.Core.Auth;
using Cove.Plugins;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Api;

public sealed class RequestPathPrincipalTests
{
    [Fact]
    public async Task Undo_RunsEveryCommandUnderTheCallersPrincipal_NotSystem()
    {
        using var dir = new TempDir();
        await using var library = await LibraryDatabase.CreateAsync();

        string folderPath = dir.Root.Replace('\\', '/');
        int videoId;
        await using (var seed = library.NewContext())
        {
            (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(seed, folderPath, "raw.mkv", "My Film");
        }

        File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

        var ext = await LoadedExtensionAsync(library);

        // A real forward rename first, so the undo has a journalled batch to replay and its whole spine
        // - the journal read, the restore and the row retirement - runs inside the observation window.
        // The batch itself is detached and elevated; DetachedElevationTests is where that is asserted.
        await ext.RunRenamerBatchAsync(RenamerFileKind.Video, [videoId], new FakeJobProgress(), default);
        Assert.True(File.Exists(Path.Combine(dir.Root, "My Film.mkv")));

        var caller = Caller(Permissions.VideosRead, Permissions.VideosWrite);
        library.Principals.Set(caller);
        library.CommandsExecuted.Clear();

        await ext.UndoAsync(library.Principals, library.Authorization, default);

        AssertRanEntirelyAsTheCaller(library);

        // The undo actually put the file back, so the commands above were the restore's and not a
        // handler that returned early with nothing to do.
        Assert.True(File.Exists(Path.Combine(dir.Root, "raw.mkv")));
    }

    [Fact]
    public async Task ScanRows_RunsEveryCommandUnderTheCallersPrincipal_NotSystem()
    {
        using var dir = new TempDir();
        await using var library = await LibraryDatabase.CreateAsync();

        string folderPath = dir.Root.Replace('\\', '/');
        await using (var seed = library.NewContext())
        {
            await ExecutorTestSeed.SeedVideoAsync(seed, folderPath, "raw.mkv", "My Film");
        }

        var ext = await LoadedExtensionAsync(library);

        library.Principals.Set(Caller(Permissions.VideosRead));
        library.CommandsExecuted.Clear();

        var page = await ext.ScanRowsAsync(null, library.Principals, default);

        AssertRanEntirelyAsTheCaller(library);

        // A 403 or a 400 would also record no command, so name the outcome: the page was served.
        Assert.NotNull(page);
    }

    [Fact]
    public async Task LastBatch_RunsEveryCommandUnderTheCallersPrincipal_NotSystem()
    {
        await using var library = await LibraryDatabase.CreateAsync();
        var ext = await LoadedExtensionAsync(library);

        library.Principals.Set(Caller(Permissions.VideosRead));
        library.CommandsExecuted.Clear();

        // The paths-free undo probe the panel polls. It opens its own scope and reads the journal's batch
        // row, and it is a third handler that does so unelevated - the source's prose names only two.
        var summary = await ext.LastBatchAsync(library.Principals, default);

        AssertRanEntirelyAsTheCaller(library);
        Assert.NotNull(summary);
    }

    [Fact]
    public async Task LastBatch_AdmitsACallerWhoCanReadOnlyTexts()
    {
        await using var library = await LibraryDatabase.CreateAsync();
        var ext = await LoadedExtensionAsync(library);
        library.Principals.Set(Caller(Permissions.TextsRead));

        var result = await ext.LastBatchAsync(library.Principals, default);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<global::Renamer.Contracts.LastBatchSummary>>(result.Result);
    }

    // Every command recorded since the last clear ran as the caller - a User - and none as System,
    // over a non-empty recording.
    private static void AssertRanEntirelyAsTheCaller(LibraryDatabase library)
    {
        var recorded = library.CommandsExecuted.ToList();

        // Non-empty first: a handler that reached no database at all would satisfy every verdict below.
        Assert.NotEmpty(recorded);
        Assert.All(recorded, c => Assert.Equal(PrincipalKind.User, c.Principal));
        Assert.DoesNotContain(PrincipalKind.System, recorded.Select(c => c.Principal));
    }

    // A present, unprivileged-beyond-these-keys user principal - never a null accessor.
    private static CovePrincipal Caller(params string[] permissions) => new()
    {
        UserId = 1,
        Username = "caller",
        Kind = PrincipalKind.User,
        Roles = new HashSet<string>(),
        Permissions = new HashSet<string>(permissions),
    };

    private static async Task<global::Renamer.Renamer> LoadedExtensionAsync(LibraryDatabase library)
    {
        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(
            new RenamerOptions { FilenameTemplate = "$title", SameVolumeConcurrency = 1 });
        var ext = RenamerFixture.Create();
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(library.BuildProvider());
        return ext;
    }
}
