using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Data;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

/// <summary>
/// The auto-renamer hook — the per-edit path a <c>video.updated</c> event drives — and the six
/// properties that keep it from acting when it must not.
/// </summary>
/// <remarks>
/// Every case here fires <see cref="IEventExtension.OnEventAsync"/> at a seeded library and asserts a
/// consequence: the principal the database work runs under, whether the handler contains its own
/// failure, whether the opt-in flag and the require-fields gate suppress it, whether it re-enters
/// itself, whether it opens a journal batch an undo can replay, and whether a routing rule relocates
/// the item.
/// <para>
/// Which media kinds are hooked at all is asserted separately, in
/// <see cref="AutoRenamerEventRegistrationTests"/>. That suite drives the hook surface through
/// <c>Cove.Plugins</c> alone, so it is the only hook coverage that compiles on the cove-absent
/// continuous-integration leg — everything in this file needs a real <c>CoveContext</c> and is
/// <c>Compile Remove</c>d there.
/// </para>
/// </remarks>
[Trait("Tier", "L1")]
public sealed class AutoRenamerHookTests
{
    /// <summary>
    /// An <see cref="IExtensionStore"/> whose every read throws — standing in for any inner failure
    /// (a transient store/DB error) on the auto-renamer path. The handler loads options from the store
    /// as its first step, so this reliably exercises the catch.
    /// </summary>
    private sealed class ThrowingStore : IExtensionStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => throw new InvalidOperationException("store unavailable");

        public Task SetAsync(string key, string value, CancellationToken ct = default)
            => throw new InvalidOperationException("store unavailable");

        public Task DeleteAsync(string key, CancellationToken ct = default)
            => throw new InvalidOperationException("store unavailable");

        public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("store unavailable");
    }

    /// <summary>
    /// The hook's background database work runs as System.
    /// </summary>
    /// <remarks>
    /// The host dispatches entity events fire-and-forget, so this flow carries whichever principal made
    /// the edit. Under a present but under-privileged one — the dangerous case, and what this test
    /// drives — Cove's authorization filters return zero rows SUCCESSFULLY: the hook then silently
    /// renames nothing, which is indistinguishable from an empty library. A dispatch carrying NO
    /// principal is the safe case rather than the dangerous one, because <c>CoveContext</c> bypasses
    /// those filters for a null principal exactly as it does for System — which is why an absent
    /// principal must never stand in for an unprivileged one here.
    /// <para>
    /// Asserted on the principal AT THE COMMAND rather than on a row count, for the reason
    /// <c>Library</c> documents: <c>CoveContext</c> installs those filters only under Npgsql, so this
    /// tier cannot reproduce the zero-row consequence — and the e2e tier runs with auth off, so it
    /// cannot either. The principal in effect when the reader executes IS the fact the filters consult,
    /// and it stays true whichever provider is underneath.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheHooksReads_ExecuteUnderSystem_AndLeaveTheCallersPrincipalBehindThem()
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

    /// <summary>
    /// The hook must contain its own failures.
    /// </summary>
    /// <remarks>
    /// The host dispatches these events fire-and-forget and only logs an escaped exception generically,
    /// with no entity context, so a handler that lets a failure bubble produces an opaque, repeating
    /// host-log error on every update. The handler instead catches, records the failure with the entity
    /// context, and returns — so the host-facing <c>OnEventAsync</c> completes normally even when the
    /// inner path throws.
    /// </remarks>
    [Fact]
    public async Task InnerPathThrows_HandlerCatches_DoesNotPropagateToHost()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<DbContext>(db);
            services.AddSingleton<IEventBus>(new CapturingEventBus());
            var provider = services.BuildServiceProvider();

            var ext = RenamerFixture.Create();
            ((IStatefulExtension)ext).SetStore(new ThrowingStore()); // first store read throws.
            await ext.InitializeAsync(provider);

            // The host calls OnEventAsync; the inner option-load throws. The handler must swallow it
            // so the host's dispatch loop is not handed a context-free exception. No throw == pass.
            var ex = await Record.ExceptionAsync(
                () => ext.OnEventAsync(new ExtensionEvent("video.updated", "video", 1), default));
            Assert.Null(ex);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Auto-renaming is opt-in: with the flag OFF (the default) an update does nothing at all.
    /// </summary>
    [Fact]
    public async Task FlagOff_FiringUpdated_PerformsNoRenamer_NoEvents()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // Name differs from the "$title" render, so ONLY the OFF flag can be why nothing happens.
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

            // Default options: AutoRenamerOnUpdate is false.
            var (ext, bus, _) = await EventTestHarness.BuildAsync(db, new RenamerOptions());

            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("raw.mkv", basename);                              // DB untouched
            Assert.True(File.Exists(Path.Combine(dir.Root, "raw.mkv")));     // disk untouched
            Assert.False(File.Exists(Path.Combine(dir.Root, "My Film.mkv")));
            Assert.Empty(bus.Published);                                     // no save → no event
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// With the flag ON but the planner's require-fields gate excluding the item, the hook still does
    /// nothing — no junk names on incomplete metadata.
    /// </summary>
    [Fact]
    public async Task FlagOn_ButRequireFieldsGateExcludes_PerformsNoRenamer()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // Empty title → the require-fields ["title"] gate excludes this item (SkipGated).
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw.mkv", title: "");
            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

            // FilenameAsTitle forced off so the empty title is NOT rescued by the basename fallback
            // (which now defaults on) — this case proves the require-fields gate excludes the item.
            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                RequiredFields = ["title"],
                FilenameAsTitle = false,
            };
            var (ext, bus, _) = await EventTestHarness.BuildAsync(db, options);

            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("raw.mkv", basename);                           // gated → unchanged
            Assert.True(File.Exists(Path.Combine(dir.Root, "raw.mkv")));
            Assert.Empty(bus.Published);                                 // no save → no event
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// The "executes once" half of the re-entrancy story: the first event renames the file once (disk +
    /// DB); a SECOND event — standing in for the re-raised <c>video.updated</c> the executor's save
    /// produces — finds an all-NoOp plan and leaves the terminal state stable with no further churn and
    /// no new published event.
    /// </summary>
    [Fact]
    public async Task FlagOn_NameDiffers_RenamesOnce_ThenReentryIsStableNoOp()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                FilenameTemplate = "$title",
            };
            var (ext, bus, _) = await EventTestHarness.BuildAsync(db, options);

            // First event: renames raw.mkv → My Film.mkv (one acting item ⇒ one publish).
            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            Assert.True(File.Exists(Path.Combine(dir.Root, "My Film.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "raw.mkv")));
            var (basenameAfterFirst, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("My Film.mkv", basenameAfterFirst);
            Assert.Single(bus.Published);            // exactly one save → exactly one re-raise

            // Second event (the re-raised update): now all-NoOp → guard short-circuits, no churn.
            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            var (basenameAfterSecond, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("My Film.mkv", basenameAfterSecond);                 // stable terminal state
            Assert.True(File.Exists(Path.Combine(dir.Root, "My Film.mkv")));
            Assert.Single(bus.Published);            // no additional event from the re-entry
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// The re-entrancy story over a destination pair that NEVER reaches a fixed point: one action moves
    /// the file exactly once, the event that move re-raised moves it nowhere, and a later genuine edit is
    /// still processed.
    /// </summary>
    /// <remarks>
    /// A source-path rule sends the file out of the folder the rule keys on, and the default
    /// <i>Where files go</i> is rooted at exactly that folder with an EMPTY folder template — so it names
    /// the very folder the rule empties. The rule matches on where the file IS, so after the rule fires it
    /// stops matching and the default reclaims the item; the two alternate rather than compete and no pass
    /// is ever all-NoOp. That is what separates this case from
    /// <see cref="FlagOn_NameDiffers_RenamesOnce_ThenReentryIsStableNoOp"/>: there the second pass has
    /// nothing to do, here it WOULD act, so the plan-is-empty condition can never stop the loop and only a
    /// suppression scoped to the handler's own save can.
    /// <para>
    /// A non-empty default template would land the file in a subfolder the rule's pattern does not name,
    /// which converges in three passes; the emptiness here is what makes the pair non-converging and is
    /// the whole arrangement.
    /// </para>
    /// <para>
    /// Every expected path below is written out from the arrangement by hand. Asking the resolver or the
    /// planner where the file should be would produce an expectation that agrees with the code under test
    /// however far the two drift, which is the one failure these assertions exist to catch.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task FlagOn_NonConvergingDestinationPair_OneHopPerAction_NoBounceOnItsOwnEvent()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // Sibling folders under one temp root → same volume, so the atomic File.Move path applies.
            string libraryFolder = Path.Combine(dir.Root, "library");
            string sortedRoot = Path.Combine(dir.Root, "sorted");
            Directory.CreateDirectory(libraryFolder);

            string libraryFwd = libraryFolder.Replace('\\', '/');
            string sortedFwd = sortedRoot.Replace('\\', '/');

            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, libraryFwd, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(libraryFolder, "raw.mkv"), "bytes");

            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                FilenameTemplate = "$title",
                AllowedRoots = [libraryFwd, sortedFwd],
                // The pair that never settles: the rule sends the file OUT of libraryFolder, and the
                // default is rooted AT libraryFolder with nothing rendered under it, so it sends the file
                // straight back.
                FolderRoot = libraryFwd,
                FolderTemplate = "",
                PathDestinations =
                    [new PathDestinationRule
                    {
                        Pattern = libraryFwd, Dest = Dests.At(sortedFwd, "Films"), IsRegex = false,
                    }],
            };
            var (ext, bus, _) = await EventTestHarness.BuildAsync(db, options, libraryFwd, sortedFwd);

            // Both destinations, transcribed from the arrangement above — never computed.
            string atRule = Path.Combine(sortedRoot, "Films", "My Film.mkv");
            string atDefault = Path.Combine(libraryFolder, "My Film.mkv");

            // (1) One action, one hop: the rule matches the file's folder and relocates it once.
            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            Assert.True(File.Exists(atRule), $"the matched rule did not relocate the file to {atRule}");
            Assert.False(File.Exists(Path.Combine(libraryFolder, "raw.mkv")));
            var (_, pathAfterFirst) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal($"{sortedFwd}/Films/My Film.mkv", pathAfterFirst.Replace('\\', '/'));
            Assert.True(
                bus.Published.Count == 1,
                $"one save must re-raise exactly one event, but {bus.Published.Count} were published");

            // (2) The event that save re-raised. On this pair the plan is NOT empty — the default would
            //     take the file back — so nothing but the self-save suppression can stop it.
            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            Assert.True(
                File.Exists(atRule),
                "the file bounced back on the event its own save raised — the auto-renamer re-entered "
                    + "itself instead of ignoring an item it had just saved");
            Assert.False(
                File.Exists(atDefault),
                $"the file bounced back to {atDefault} on the event its own save raised");
            var (_, pathAfterReentry) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal($"{sortedFwd}/Films/My Film.mkv", pathAfterReentry.Replace('\\', '/'));
            Assert.True(
                bus.Published.Count == 1,
                "the re-entrant event saved and re-raised again, which is the runaway: "
                    + $"{bus.Published.Count} events published where one action happened");

            // (3) A LATER genuine edit — not the re-raised one. It must be processed: the suppression is
            //     scoped to the action that armed it, not a mode the handler stays in. Without this
            //     assertion a suppression that never released would pass (1) and (2) and mute the hook
            //     permanently.
            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            Assert.True(
                File.Exists(atDefault),
                "a genuine later edit was swallowed — the self-save suppression never released, so the "
                    + "auto-renamer is now permanently muted for this item");
            Assert.False(File.Exists(atRule), "the later edit left a copy at the rule's destination");
            var (_, pathAfterThird) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal($"{libraryFwd}/My Film.mkv", pathAfterThird.Replace('\\', '/'));
            Assert.True(
                bus.Published.Count == 2,
                $"the later edit's own save must re-raise one event, but the bus holds {bus.Published.Count}");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// The load-bearing safety property: with the flag ON but the file ALREADY named exactly what the
    /// template renders, the plan is all-NoOp, so firing the handler performs ZERO saves and the bus
    /// records ZERO published events.
    /// </summary>
    /// <remarks>
    /// Because the executor's save is the only thing that re-raises <c>video.updated</c>, zero events
    /// proves the save→event→re-enter loop can never START. That is a different claim from
    /// <see cref="FlagOn_NameDiffers_RenamesOnce_ThenReentryIsStableNoOp"/>, which drives the second
    /// event through a handler that has already acted once and so proves the loop does not CONTINUE.
    /// </remarks>
    [Fact]
    public async Task FlagOn_AlreadyCorrectName_PerformsZeroSaves_ZeroEvents()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // The basename already equals what "$title" renders → every file is NoOp.
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "My Film.mkv", "My Film");
            File.WriteAllText(Path.Combine(dir.Root, "My Film.mkv"), "bytes");

            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                FilenameTemplate = "$title",
            };
            var (ext, bus, _) = await EventTestHarness.BuildAsync(db, options);

            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            // ZERO published events ⇒ no executor save ran ⇒ no re-raised event ⇒ the loop is impossible.
            Assert.Empty(bus.Published);

            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("My Film.mkv", basename);                           // unchanged
            Assert.True(File.Exists(Path.Combine(dir.Root, "My Film.mkv")));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// The data-recovery spine: a rename driven by the event must open its own journal batch, and the
    /// row it writes must carry the PARENT entity id alongside the file id so /undo republishes the
    /// right entity.
    /// </summary>
    /// <remarks>
    /// The decoy video makes <c>videoId ≠ fileId</c>, so a row that confused the two is distinguishable
    /// from a correct one.
    /// </remarks>
    [Fact]
    public async Task AutoRename_OpensItsOwnBatch_RowCarriesEntityAndFileId_UndoRestoresDiskAndDb()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // Offset the Video id sequence so videoId ≠ fileId: the misparse writes EntityId = FileId,
            // so distinct ids are what prove the row parsed as the correct 4-field shape.
            await SeedDecoyVideoAsync(db);
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw.mkv", "My Film");
            Assert.NotEqual(videoId, fileId);

            string oldFull = Path.Combine(dir.Root, "raw.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                FilenameTemplate = "$title",
            };
            var (ext, _, _) = await EventTestHarness.BuildAsync(db, options);

            // Drive the hook for the one entity that WILL act: raw.mkv → My Film.mkv.
            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            Assert.True(File.Exists(newFull));
            Assert.False(File.Exists(oldFull));

            // (a) A fresh reader over the journal sees exactly one batch with rows, carrying the kind.
            var readBack = new CoveRevertJournal(db);
            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(readBack);
            Assert.NotNull(batch);
            Assert.Equal(RenamerFileKind.Video, batch.Kind);

            // (b) EntityId is the VIDEO id and FileId is the FILE id, and they differ — a row that
            // confused the two would have EntityId == FileId.
            var entry = Assert.Single(batch.Rows);
            Assert.Equal(videoId, entry.EntityId);
            Assert.Equal(fileId, entry.FileId);
            Assert.NotEqual(entry.EntityId, entry.FileId);

            // (c) Reverse-replay the batch restores disk + DB.
            var port = new CoveRenamerDataPort(db);
            var undoBus = new CapturingEventBus();
            var result = await new UndoReplayer(port, undoBus, new DiskMover()).RevertAsync(batch, default);

            Assert.Equal(1, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);

            Assert.True(File.Exists(oldFull), "file restored to old path");
            Assert.False(File.Exists(newFull), "new path gone after undo");
            Assert.Equal("video-bytes", File.ReadAllText(oldFull));

            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("raw.mkv", basename);
            Assert.Equal(folderPath + "/raw.mkv", path);

            // The undo republished the entity id from the row (the video id, not the file id).
            var evt = Assert.IsType<EntityEvent>(Assert.Single(undoBus.Published));
            Assert.Equal(videoId, evt.EntityId);
            Assert.NotEqual(fileId, evt.EntityId);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// A matched routing rule must relocate the just-edited item to its configured destination — the
    /// SAME on-disk outcome the manual batch and <c>/preview</c> produce.
    /// </summary>
    /// <remarks>
    /// Before the fix the hook called the empty-lookups overload, so auto-renames silently never
    /// relocated even when a matching destination rule was configured.
    /// </remarks>
    [Fact]
    public async Task FlagOn_MatchedSourcePathRule_RelocatesToRoutedDestination()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // src and dest are sibling folders under one temp root → same volume, so the DiskMover
            // atomic File.Move path applies (no cross-volume mover needed in this slice).
            string srcFolder = Path.Combine(dir.Root, "incoming");
            string destRoot = Path.Combine(dir.Root, "sorted");
            Directory.CreateDirectory(srcFolder);

            string srcPathFwd = srcFolder.Replace('\\', '/');
            string destRootFwd = destRoot.Replace('\\', '/');

            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcPathFwd, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(srcFolder, "raw.mkv"), "bytes");

            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                FilenameTemplate = "$title",
                AllowedRoots = [srcPathFwd, destRootFwd],
                PathDestinations =
                    [new PathDestinationRule
                    {
                        Pattern = srcPathFwd, Dest = Dests.At(destRootFwd, "Films"), IsRegex = false,
                    }],
            };
            var (ext, bus, _) = await EventTestHarness.BuildAsync(db, options, destRootFwd);

            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            // The matched route relocated the file to destRoot/Films/My Film.mkv — NOT in place.
            string expected = Path.Combine(destRoot, "Films", "My Film.mkv");
            Assert.True(File.Exists(expected), $"expected routed file at {expected}");
            Assert.False(File.Exists(Path.Combine(srcFolder, "raw.mkv")));

            var (_, pathAfter) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Contains("sorted/Films/My Film.mkv", pathAfter.Replace('\\', '/'));
            Assert.Single(bus.Published); // one acting move → one re-raised event
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// The other half of routing: an item matching NO rule is renamed in place and never relocated, so
    /// a metadata edit cannot dribble the library toward a catch-all destination — there is none.
    /// </summary>
    [Fact]
    public async Task FlagOn_UnmatchedItem_StaysInPlace_NeverRelocated()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = dir.Root;
            string srcPathFwd = srcFolder.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcPathFwd, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(srcFolder, "raw.mkv"), "bytes");

            // No matching rule → source-confine: an in-place renamer (My Film.mkv) is fine, but the file
            // must stay in its own folder. Relocating requires an explicit rule; there is no catch-all.
            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                FilenameTemplate = "$title",
            };
            var (ext, _, _) = await EventTestHarness.BuildAsync(db, options);

            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            // Renamed in place. Asserted as the WHOLE stored path rather than as the absence of one
            // destination name: with no catch-all to name, an absence assertion would hold however far
            // the file had moved.
            Assert.True(File.Exists(Path.Combine(srcFolder, "My Film.mkv")));
            var (_, pathAfter) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal($"{srcPathFwd}/My Film.mkv", pathAfter.Replace('\\', '/'));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Seeds one throwaway Video so the next <see cref="ExecutorTestSeed.SeedVideoAsync"/> hands back a
    /// Video id one ahead of its VideoFile id — guaranteeing videoId ≠ fileId.
    /// </summary>
    private static async Task SeedDecoyVideoAsync(CoveContext db)
    {
        db.Set<Video>().Add(new Video { Title = "decoy", Organized = true });
        await db.SaveChangesAsync();
    }
}
