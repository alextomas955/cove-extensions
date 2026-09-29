using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Elevation;

public sealed class DetachedElevationTests
{
    // The hand-written legacy journal header shape: run, opened-at, kind, status.
    private static readonly DateTime LegacyOpened = new(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task TheLoadTimeJournalAssertion_RunsEveryCommandAsSystem()
    {
        await using var library = await LibraryDatabase.CreateAsync();
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
        await using var library = await LibraryDatabase.CreateAsync();
        var store = new FakeStore();
        await store.SetAsync(JournalBlobMigration.SchemaKey, JournalBlobMigration.CurrentSchema);
        await store.SetAsync(
            JournalBlobMigration.Key,
            string.Join("\n", $"#batch|R1|{LegacyOpened.Ticks}|Video|open", "7|70|/lib/a.mkv"));

        var ext = RenamerFixture.Create();
        ((IStatefulExtension)ext).SetStore(store);

        library.Principals.Set(CovePrincipal.Anonymous());
        library.CommandsExecuted.Clear();

        await ext.InitializeAsync(library.BuildProvider());

        AssertRanEntirelyAsSystem(library);

        // Read back after the assertion, because this read runs unelevated and would otherwise be
        // recorded alongside the load's commands. It is here so the case cannot pass on the migration
        // having done nothing: the row it moved has to be in the journal table.
        await using var db = library.NewContext();
        var migrated = await new CoveRevertJournal(db).ReadUndoTargetAsync();
        Assert.NotNull(migrated);
        Assert.Equal("R1", migrated.Value.OperationId);
    }

    [Fact]
    public async Task TheBatchPlanningRead_RunsEveryCommandAsSystem()
    {
        using var dir = new TempDir();
        await using var library = await LibraryDatabase.CreateAsync();

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

        await ext.RunRenamerBatchAsync(RenamerFileKind.Video, [videoId], new FakeJobProgress(), default);

        AssertRanEntirelyAsSystem(library);

        await using var db = library.NewContext();
        Assert.Null(await new CoveRevertJournal(db).ReadUndoTargetAsync());
    }

    [Fact]
    public async Task TheBatchFolderPreCreateAndExecutor_RunEveryCoveReadAsSystem()
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

        // A routed destination folder that does not exist yet is what makes the pre-create span issue a
        // command at all; an in-place rename skips it entirely.
        var options = TitleOnlyOptions() with
        {
            PathDestinations =
                [new PathDestinationRule
                {
                    Pattern = folderPath, Dest = Dest.At(folderPath, "sorted"), IsRegex = false,
                }],
        };

        var (ext, _) = await LoadedExtensionAsync(library, options, folderPath);

        await ext.RunRenamerBatchAsync(RenamerFileKind.Video, [videoId], new FakeJobProgress(), default);

        await AssertEveryCoveReadRanAsSystemAsync(library);

        // Read back after the assertion: the pre-create made the destination folder row, and the
        // executor moved the file into it.
        await using var db = library.NewContext();
        Assert.Equal(1, await db.Set<Folder>().AsNoTracking().CountAsync(f => f.Path == folderPath + "/sorted"));
        Assert.True(File.Exists(Path.Combine(dir.Root, "sorted", "My Film.mkv")));
    }

    // A queued body is run by a processor started before any request, so it enters carrying no
    // principal at all rather than the unprivileged one the load arms with.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheScanLibraryJobBody_RunsEveryCommandAsSystem(bool enteredWithNoPrincipal)
    {
        await using var library = await LibraryDatabase.CreateAsync();
        var (ext, _) = await LoadedExtensionAsync(library, TitleOnlyOptions());
        var prior = EnterWith(library, enteredWithNoPrincipal);

        // An empty library still loads the id list, which is the command this case observes; a seeded
        // one would add the planner's reads without changing what is being asserted.
        await ext.RunScanLibraryJobAsync(
            library.Principals.Current, [RenamerFileKind.Video], null, new FakeJobProgress(), default);

        AssertRanEntirelyAsSystem(library, prior);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheRenamerLibraryJobBody_RunsEveryCommandAsSystem(bool enteredWithNoPrincipal)
    {
        await using var library = await LibraryDatabase.CreateAsync();
        var (ext, _) = await LoadedExtensionAsync(library, TitleOnlyOptions());
        var prior = EnterWith(library, enteredWithNoPrincipal);

        await ext.RunRenamerLibraryJobAsync(
            library.Principals.Current, [RenamerFileKind.Video], new FakeJobProgress(), default);

        AssertRanEntirelyAsSystem(library, prior);
    }

    // Undoes the load's arming when the case enters with no principal, and returns the kind the
    // body must put back.
    private static PrincipalKind? EnterWith(LibraryDatabase library, bool noPrincipal)
    {
        if (!noPrincipal)
        {
            return PrincipalKind.Anonymous;
        }

        library.Principals.Set(null);
        library.CommandsExecuted.Clear();
        return null;
    }

    // Every command recorded since the last clear ran as System, and at least one was recorded -
    // plus the caller's own principal is back, because elevation is a span and not a mode. library:
    // The observed database and its principal accessor. expectedPriorKind: The principal kind in
    // effect when the body was entered, or null when there was none. The restore is asserted
    // against whatever the caller actually had rather than against a fixed Anonymous, because a
    // queued body enters with none and putting back a default instead of nothing would be a
    // different bug wearing the same green.
    private static void AssertRanEntirelyAsSystem(
        LibraryDatabase library, PrincipalKind? expectedPriorKind = PrincipalKind.Anonymous)
    {
        var recorded = library.CommandsExecuted.ToList();

        // Non-empty first: an all-System verdict over zero commands proves nothing, and a body that
        // never reached the database is the failure that produces one.
        Assert.NotEmpty(recorded);
        Assert.All(recorded, c => Assert.Equal(PrincipalKind.System, c.Principal));
        Assert.Equal(expectedPriorKind, library.Principals.Current?.Kind);
    }

    // The batch core's variant: every command against a table COVE owns ran as System, and nothing
    // that ran unelevated reached one. The batch holds a scope the source states is deliberately
    // not elevated - the one the shared undo journal owns - on the grounds that the journal's
    // tables are the extension's own and carry none of Cove's per-principal query filters, so
    // System has nothing there to unlock. A plain all-System verdict over this window would
    // therefore assert a property the code does not have, and pass only until someone noticed. The
    // second assertion is what stops that exception swallowing the rule: a Cove-entity read that
    // stopped being elevated cannot hide inside it. Both arms ask NamesATableCoveOwns rather than a
    // question about the extension's own set, and TablesByOwnershipAsync states why that difference
    // is load-bearing.
    private static async Task AssertEveryCoveReadRanAsSystemAsync(LibraryDatabase library)
    {
        var recorded = library.CommandsExecuted.ToList();
        var tables = await TablesByOwnershipAsync(library);

        var coveReads = recorded.Where(c => NamesATableCoveOwns(tables, c)).ToList();

        // Non-empty first, on the set the assertion is about: an all-System verdict over no Cove read at
        // all is the vacuous pass, and a case whose body never reached Cove's own tables produces one.
        Assert.NotEmpty(coveReads);
        Assert.All(coveReads, c => Assert.Equal(PrincipalKind.System, c.Principal));
        Assert.All(
            recorded.Where(c => c.Principal != PrincipalKind.System),
            c => Assert.False(
                NamesATableCoveOwns(tables, c),
                $"an unelevated command reached a table Cove owns: {c.Sql}"));
        Assert.Equal(PrincipalKind.Anonymous, library.Principals.Current!.Kind);
    }

    private readonly record struct TablesByOwnership(IReadOnlySet<string> Own, IReadOnlySet<string> Cove);

    // The model's tables split into this extension's own and - as the complement of that same
    // enumeration - Cove's, with both sides asserted non-empty before either is handed back.
    // Ownership is taken from the model the extension itself configures, so no table name is
    // restated here to go stale when one is renamed; the complement inherits that property rather
    // than needing a list of its own. Non-empty on both sides, and before either is used: a side
    // that came back empty turns the predicate over it into a constant, and a verdict resting on a
    // constant is the same vacuous pass this class's other non-empty assertions exist to refuse.
    private static async Task<TablesByOwnership> TablesByOwnershipAsync(LibraryDatabase library)
    {
        HashSet<string> own, cove;
        await using (var db = library.NewContext())
        {
            var byOwnership = db.Model.GetEntityTypes()
                .Select(t => (
                    Own: t.ClrType.Assembly == typeof(global::Renamer.Renamer).Assembly,
                    Table: t.GetTableName()))
                .Where(x => x.Table is not null)
                .ToList();

            own = [.. byOwnership.Where(x => x.Own).Select(x => x.Table!)];
            cove = [.. byOwnership.Where(x => !x.Own).Select(x => x.Table!)];
        }

        Assert.NotEmpty(own);
        Assert.NotEmpty(cove);
        return new TablesByOwnership(own, cove);
    }

    // Whether c's SQL reaches a table Cove owns. A command that also names one of this extension's
    // tables still counts: naming an own table cannot excuse naming a Cove one. The match is a
    // case-insensitive substring of the statement text.
    private static bool NamesATableCoveOwns(TablesByOwnership tables, LibraryDatabase.ExecutedCommand c) =>
        tables.Cove.Any(table => c.Sql.Contains(table, StringComparison.OrdinalIgnoreCase));

    // The shipped extension, loaded over library with options saved, then armed for observation:
    // the caller's principal set to a present-but-unprivileged one and the recording cleared, so
    // what a case asserts on is its own exercise and not the load.
    private static async Task<(global::Renamer.Renamer ext, FakeStore store)> LoadedExtensionAsync(
        LibraryDatabase library, RenamerOptions options, params string[] libraryRoots)
    {
        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(options);
        var ext = RenamerFixture.Create();
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(library.BuildProvider(libraryRoots));

        library.Principals.Set(CovePrincipal.Anonymous());
        library.CommandsExecuted.Clear();
        return (ext, store);
    }

    [Fact]
    public async Task TheHooksReads_ExecuteUnderSystem_AndLeaveTheCallersPrincipalBehindThem()
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

        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(
            new RenamerOptions { AutoRenamerOnUpdate = true, FilenameTemplate = "$title" });

        var ext = RenamerFixture.Create();
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(library.BuildProvider());

        // Present but unprivileged, which is the case the elevation exists for. Leaving the accessor
        // empty instead would prove the safe case: no principal bypasses the filters anyway.
        library.Principals.Set(CovePrincipal.Anonymous());
        library.CommandsExecuted.Clear();

        await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

        // Non-empty first: an all-System verdict over zero commands would be a vacuous pass, and a hook
        // that never reached the database at all is exactly the failure this is here to catch.
        Assert.NotEmpty(library.CommandsExecuted);
        Assert.All(library.CommandsExecuted, c => Assert.Equal(PrincipalKind.System, c.Principal));

        // Elevation is a span, not a mode: the caller's principal is put back afterwards.
        Assert.Equal(PrincipalKind.Anonymous, library.Principals.Current!.Kind);
    }

    // A title-only template so a seeded, height-less row renders a predictable name, and one
    // same-volume worker because LibraryDatabase hands every scope a context over one SQLite
    // connection - production draws a connection per scope, so serializing here removes a
    // harness-only race without changing the path under test.
    private static RenamerOptions TitleOnlyOptions() =>
        new() { FilenameTemplate = "$title", SameVolumeConcurrency = 1 };
}
