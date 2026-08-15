using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Jobs;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

/// <summary>
/// One assertion per DETACHED entry point in this extension: the database commands its body runs against
/// Cove's own tables execute under the System principal.
/// </summary>
/// <remarks>
/// The assertions are keyed to ENTRY POINTS, not to elevation call sites, because a site count goes
/// stale the moment a body grows another scope while the set of detached bodies does not. The detached
/// entry points are the load-time journal assertion, the stored-journal migration, the stored-options
/// conversion (covered by <c>OptionsMigrationInitializeTests</c>), the shared batch core's three
/// elevated spans — its planning read, its destination-folder pre-create and its per-worker executor —
/// the auto-renamer hook (covered by
/// <see cref="AutoRenamerHookTests.TheHooksReads_ExecuteUnderSystem_AndLeaveTheCallersPrincipalBehindThem"/>),
/// and the two job bodies in
/// <c>Renamer.Api.cs</c>. Adding a further span inside one of those bodies needs no case here: the case
/// for that body already asserts over EVERY command it ran.
/// <para>
/// Each case asserts two things, and the second is what makes the first mean anything: the principal at
/// every command is the expected one, AND at least one command was recorded. A verdict over an empty
/// list is a vacuous pass, and a body that never reached the database is exactly the mistake that
/// produces one.
/// </para>
/// <para>
/// "Detached" is not the same as "elevated", and the batch core is where the difference shows: it also
/// opens a scope the source states is deliberately NOT elevated, the one the shared undo journal owns.
/// See <see cref="AssertEveryCoveReadRanAsSystemAsync"/> for why that makes a flat all-System verdict
/// wrong over that window, and what replaces it.
/// </para>
/// <para>
/// Each case starts from <see cref="CovePrincipal.Anonymous"/> — present but unprivileged. That is the
/// dangerous case and the reason the elevation exists: <c>CoveContext</c> bypasses its authorization
/// filters for a null principal as well as for System, so a case constructing "no principal" would
/// prove the SAFE case while reading as coverage.
/// </para>
/// <para>
/// The proof is the principal AT THE COMMAND rather than a row count, and on the queued-job path it is
/// the only proof that can fail at any tier. Two measured facts, in the two places the row count fails
/// for different reasons: <c>CoveContext</c> installs its authorization filters only under Npgsql, so
/// SQLite cannot reproduce the zero-row consequence at all; and Cove starts its exclusive-job queue
/// processor at host startup while holding the principal in a static <c>AsyncLocal</c>, so a queued
/// body carries no ambient principal — deleting the elevation from one was measured to change no row
/// count anywhere, end to end, against a live host under auth. What that leaves is these assertions.
/// </para>
/// </remarks>
[Trait("Tier", "L1")]
[Collection(CoveDataExtensionScope.CollectionName)]
public sealed class DetachedElevationTests
{
    /// <summary>The hand-written legacy journal header shape: run, opened-at, kind, status.</summary>
    private static readonly DateTime LegacyOpened = new(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task TheLoadTimeJournalAssertion_RunsEveryCommandAsSystem()
    {
        await using var library = await Library.CreateAsync();
        var ext = RenamerFixture.Create();
        ((IStatefulExtension)ext).SetStore(new FakeStore());

        library.Principals.Set(CovePrincipal.Anonymous());
        library.CommandsExecuted.Clear();

        await ext.InitializeAsync(library.BuildProvider());

        // An empty store leaves both migrations with nothing to do and neither touches the database, so
        // what this case observes is the reachability assertion's own read.
        AssertRanEntirelyAsSystem(library);
    }

    [Fact]
    public async Task TheStoredJournalMigration_RunsEveryCommandAsSystem()
    {
        await using var library = await Library.CreateAsync();
        var store = new FakeStore();
        await store.SetAsync(RevertLog.SchemaKey, RevertLog.CurrentSchema);
        await store.SetAsync(
            RevertLog.Key,
            string.Join("\n", $"#batch|R1|{LegacyOpened.Ticks}|Video|open", "7|70|/lib/a.mkv"));

        var ext = RenamerFixture.Create();
        ((IStatefulExtension)ext).SetStore(store);

        library.Principals.Set(CovePrincipal.Anonymous());
        library.CommandsExecuted.Clear();

        await ext.InitializeAsync(library.BuildProvider());

        AssertRanEntirelyAsSystem(library);

        // Read back AFTER the assertion, because this read runs unelevated and would otherwise be
        // recorded alongside the load's commands. It is here so the case cannot pass on the migration
        // having done nothing: the row it moved has to be in the journal table.
        await using var db = library.NewContext();
        var migrated = await new CoveRevertJournal(db).ReadUndoTargetAsync();
        Assert.NotNull(migrated);
        Assert.Equal("R1", migrated.Value.RunId);
    }

    [Fact]
    public async Task TheBatchPlanningRead_RunsEveryCommandAsSystem()
    {
        using var dir = new TempDir();
        await using var library = await Library.CreateAsync();

        string folderPath = dir.Root.Replace('\\', '/');
        int videoId;
        await using (var seed = library.NewContext())
        {
            // Already correctly named under the pinned template, so the plan is a NoOp: nothing acts,
            // the batch opens nothing, and neither the folder pre-create nor the executor reaches the
            // database. That isolation is what lets this case attribute a failure to the planning read.
            (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(seed, folderPath, "My Film.mkv", "My Film");
        }

        File.WriteAllText(Path.Combine(dir.Root, "My Film.mkv"), "bytes");

        var (ext, _) = await LoadedExtensionAsync(library, TitleOnlyOptions());

        await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", [videoId]), new FakeJobProgress(), default);

        AssertRanEntirelyAsSystem(library);

        await using var db = library.NewContext();
        Assert.Null(await new CoveRevertJournal(db).ReadUndoTargetAsync());
    }

    [Fact]
    public async Task TheBatchFolderPreCreate_RunsEveryCommandAsSystem()
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

        // A routed destination folder that does not exist yet is what makes the pre-create span issue a
        // command at all; an in-place rename skips it entirely.
        var options = TitleOnlyOptions() with
        {
            FolderTemplate = "sorted",
            AllowedRoots = [folderPath],
            PathDestinations =
                [new PathDestinationRule { Pattern = folderPath, Dest = folderPath, IsRegex = false }],
        };

        var (ext, _) = await LoadedExtensionAsync(library, options);

        await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", [videoId]), new FakeJobProgress(), default);

        await AssertEveryCoveReadRanAsSystemAsync(library);

        // The pre-create's own work, read back after the assertion: the destination folder row exists.
        await using var db = library.NewContext();
        Assert.Equal(1, await db.Set<Folder>().AsNoTracking().CountAsync(f => f.Path == folderPath + "/sorted"));
    }

    [Fact]
    public async Task TheBatchExecutor_RunsEveryCommandAsSystem()
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

        var (ext, _) = await LoadedExtensionAsync(library, TitleOnlyOptions());

        await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", [videoId]), new FakeJobProgress(), default);

        await AssertEveryCoveReadRanAsSystemAsync(library);

        // An in-place rename, so the destination-folder span issues nothing and the acting work is the
        // executor's: the file moved on disk, which is the evidence its body reached the database too.
        Assert.True(File.Exists(Path.Combine(dir.Root, "My Film.mkv")));
    }

    [Fact]
    public async Task TheScanLibraryJobBody_RunsEveryCommandAsSystem()
    {
        await using var library = await Library.CreateAsync();
        var (ext, _) = await LoadedExtensionAsync(library, TitleOnlyOptions());

        // An empty library still loads the id list, which is the command this case observes; a seeded
        // one would add the planner's reads without changing what is being asserted.
        await ext.RunScanLibraryJobAsync([RenamerFileKind.Video], null, new FakeJobProgress(), default);

        AssertRanEntirelyAsSystem(library);
    }

    [Fact]
    public async Task TheRenamerLibraryJobBody_RunsEveryCommandAsSystem()
    {
        await using var library = await Library.CreateAsync();
        var (ext, _) = await LoadedExtensionAsync(library, TitleOnlyOptions());

        await ext.RunRenamerLibraryJobAsync([RenamerFileKind.Video], new FakeJobProgress(), default);

        AssertRanEntirelyAsSystem(library);
    }

    /// <summary>
    /// Every command recorded since the last clear ran as System, and at least one was recorded — plus
    /// the caller's own principal is back, because elevation is a span and not a mode.
    /// </summary>
    private static void AssertRanEntirelyAsSystem(Library library)
    {
        var recorded = library.CommandsExecuted.ToList();

        // Non-empty FIRST: an all-System verdict over zero commands proves nothing, and a body that
        // never reached the database is the failure that produces one.
        Assert.NotEmpty(recorded);
        Assert.All(recorded, c => Assert.Equal(PrincipalKind.System, c.Principal));
        Assert.Equal(PrincipalKind.Anonymous, library.Principals.Current!.Kind);
    }

    /// <summary>
    /// The batch core's variant: every command against a table COVE owns ran as System, and everything
    /// that ran unelevated named only this extension's own tables.
    /// </summary>
    /// <remarks>
    /// The batch holds a scope the source states is deliberately NOT elevated — the one the shared undo
    /// journal owns — on the grounds that the journal's tables are the extension's own and carry none of
    /// Cove's per-principal query filters, so System has nothing there to unlock. A plain all-System
    /// verdict over this window would therefore assert a property the code does not have, and pass only
    /// until someone noticed. The second assertion is what stops that exception swallowing the rule: a
    /// Cove-entity read that stopped being elevated cannot hide inside it.
    /// <para>
    /// Which tables the extension owns is taken from the model the extension itself configures, so no
    /// table name is restated here to go stale when one is renamed.
    /// </para>
    /// </remarks>
    private static async Task AssertEveryCoveReadRanAsSystemAsync(Library library)
    {
        var recorded = library.CommandsExecuted.ToList();

        HashSet<string> ownTables;
        await using (var db = library.NewContext())
        {
            ownTables = [.. db.Model.GetEntityTypes()
                .Where(t => t.ClrType.Assembly == typeof(global::Renamer.Renamer).Assembly)
                .Select(t => t.GetTableName())
                .OfType<string>()];
        }

        Assert.NotEmpty(ownTables);

        bool NamesOnlyTheExtensionsOwnTables(Library.ExecutedCommand c) =>
            ownTables.Any(table => c.Sql.Contains(table, StringComparison.OrdinalIgnoreCase));

        var coveReads = recorded.Where(c => !NamesOnlyTheExtensionsOwnTables(c)).ToList();

        // Non-empty FIRST, on the set the assertion is about: an all-System verdict over no Cove read at
        // all is the vacuous pass, and a case whose body never reached Cove's own tables produces one.
        Assert.NotEmpty(coveReads);
        Assert.All(coveReads, c => Assert.Equal(PrincipalKind.System, c.Principal));
        Assert.All(
            recorded.Where(c => c.Principal != PrincipalKind.System),
            c => Assert.True(
                NamesOnlyTheExtensionsOwnTables(c),
                $"an unelevated command reached a table Cove owns: {c.Sql}"));
        Assert.Equal(PrincipalKind.Anonymous, library.Principals.Current!.Kind);
    }

    /// <summary>
    /// The shipped extension, loaded over <paramref name="library"/> with <paramref name="options"/>
    /// saved, then armed for observation: the caller's principal set to a present-but-unprivileged one
    /// and the recording cleared, so what a case asserts on is its own exercise and not the load.
    /// </summary>
    private static async Task<(global::Renamer.Renamer ext, FakeStore store)> LoadedExtensionAsync(
        Library library, RenamerOptions options)
    {
        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(options);
        var ext = RenamerFixture.Create();
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(library.BuildProvider());

        library.Principals.Set(CovePrincipal.Anonymous());
        library.CommandsExecuted.Clear();
        return (ext, store);
    }

    /// <summary>
    /// A title-only template so a seeded, height-less row renders a predictable name, and one same-volume
    /// worker because <see cref="Library"/> hands every scope a context over ONE SQLite connection —
    /// production draws a connection per scope, so serializing here removes a harness-only race without
    /// changing the path under test.
    /// </summary>
    private static RenamerOptions TitleOnlyOptions() =>
        new() { FilenameTemplate = "$title", SameVolumeConcurrency = 1 };
}
