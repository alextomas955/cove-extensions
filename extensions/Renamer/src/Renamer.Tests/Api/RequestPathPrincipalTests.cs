using Cove.Core.Auth;
using Cove.Plugins;
using Renamer.Jobs;
using Renamer.Options;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Api;

/// <summary>
/// The two request paths that must NOT elevate: <c>/undo</c> and <c>/scan-rows</c> run their database
/// commands under the CALLER's own principal.
/// </summary>
/// <remarks>
/// The invariant is stated in <c>Renamer.cs</c>, beside the conversion that does elevate, and it is
/// quoted here so nobody later "fixes" these two into elevation: <i>every DETACHED body in this
/// extension elevates, because none of them carries a principal of its own. The two request-path scopes
/// (/undo and /scan-rows) deliberately do not: they must stay on the caller's principal, and elevating
/// them would bypass that caller's authorization.</i>
/// <para>
/// So these are positive assertions of the opposite property, and they are load-bearing in the same way
/// their siblings in <c>DetachedElevationTests</c> are. Elevating a request path hands a restricted
/// caller rows their own read is denied — which has been measured end to end on a live host under auth,
/// where wrapping the <c>/scan-rows</c> page query in the elevation seam turned a caller's zero-row
/// answer into the whole library.
/// </para>
/// <para>
/// The caller here is a <see cref="PrincipalKind.User"/> holding exactly the permissions the handler
/// gates on — present and unprivileged beyond that, never absent. A missing principal bypasses
/// <c>CoveContext</c>'s authorization filters just as System does, so a case driven with none would
/// assert nothing about whether the path stayed on its caller.
/// </para>
/// </remarks>
[Trait("Tier", "L1")]
[Collection(CoveDataExtensionScope.CollectionName)]
public sealed class RequestPathPrincipalTests
{
    [Fact]
    public async Task Undo_RunsEveryCommandUnderTheCallersPrincipal_NotSystem()
    {
        using var dir = new TempDir();
        await using var library = await Library.CreateAsync();

        string folderPath = dir.Root.Replace('\\', '/');
        int videoId;
        await using (var seed = library.NewContext())
        {
            (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(seed, folderPath, "raw.mkv", "My Film");
        }

        File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

        var ext = await LoadedExtensionAsync(library);

        // A real forward rename first, so the undo has a journalled batch to replay and its whole spine
        // — the journal read, the restore and the row retirement — runs inside the observation window.
        // The batch itself is detached and elevated; DetachedElevationTests is where that is asserted.
        await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", [videoId]), new FakeJobProgress(), default);
        Assert.True(File.Exists(Path.Combine(dir.Root, "My Film.mkv")));

        var caller = Caller(Permissions.VideosRead, Permissions.VideosWrite);
        library.Principals.Set(caller);
        library.CommandsExecuted.Clear();

        await ext.UndoAsync(library.Principals, default);

        AssertRanEntirelyAsTheCaller(library);

        // The undo actually put the file back, so the commands above were the restore's and not a
        // handler that returned early with nothing to do.
        Assert.True(File.Exists(Path.Combine(dir.Root, "raw.mkv")));
    }

    [Fact]
    public async Task ScanRows_RunsEveryCommandUnderTheCallersPrincipal_NotSystem()
    {
        using var dir = new TempDir();
        await using var library = await Library.CreateAsync();

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

    /// <summary>
    /// Every command recorded since the last clear ran as the caller — a <see cref="PrincipalKind.User"/>
    /// — and none as System, over a non-empty recording.
    /// </summary>
    private static void AssertRanEntirelyAsTheCaller(Library library)
    {
        var recorded = library.CommandsExecuted.ToList();

        // Non-empty FIRST: a handler that reached no database at all would satisfy every verdict below.
        Assert.NotEmpty(recorded);
        Assert.All(recorded, c => Assert.Equal(PrincipalKind.User, c.Principal));
        Assert.DoesNotContain(PrincipalKind.System, recorded.Select(c => c.Principal));
    }

    /// <summary>A present, unprivileged-beyond-these-keys user principal — never a null accessor.</summary>
    private static CovePrincipal Caller(params string[] permissions) => new()
    {
        UserId = 1,
        Username = "caller",
        Kind = PrincipalKind.User,
        Roles = new HashSet<string>(),
        Permissions = new HashSet<string>(permissions),
    };

    private static async Task<global::Renamer.Renamer> LoadedExtensionAsync(Library library)
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
