using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.CanonicalGuard;

/// <summary>
/// Proof that the destination-allowlist guard is wired into the real executor write path. Every other
/// guard test calls <see cref="CanonicalPathGuard.Check"/> directly with a hand-built folder string;
/// NONE drives the real executor write path with the guard WIRED IN. This is exactly the seam where
/// the two load-bearing invariants live: the guard must cover the FULL final path (not just the
/// folder), and it must run BEFORE any disk/DB mutation.
///
/// Drives <see cref="RenamerExecutor.ExecuteAsync"/> end-to-end over a real SQLite
/// <see cref="Cove.Data.CoveContext"/> + a real <see cref="TempDir"/>, with a configured
/// <see cref="RenamerOptions.AllowedRoots"/>, and asserts:
/// <list type="bullet">
///   <item>(reject) a <c>Move</c> whose target folder is a JUNCTION inside the allowed root pointing
///   OUTSIDE it lands in <c>Skipped</c> with <see cref="RenamerStatus.SkipBlocked"/> and the guard
///   reason; the physical file does NOT move; and — proving the guard runs first — NO
///   <see cref="Folder"/> DB row is created for the rejected destination (the guard ran before
///   <c>GetOrCreateFolderIdAsync</c>);</item>
///   <item>(benign) a <c>Move</c> to a real subdirectory UNDER the allowed root still moves on disk and
///   persists its folder row.</item>
/// </list>
/// These would FAIL against a folder-only guard placed AFTER the folder-row create, and PASS with the
/// full-path guard placed before any mutation — the regression lock that the seam stays closed. The
/// junction is created via <c>cmd /c mklink /J</c> (no privilege). The reject case needs no opt-in gate on a
/// Windows dev box, and skips with a reason elsewhere.
/// </summary>
[Trait("Tier", "L1")]
[Trait("Adversarial", "Junction")]
public sealed class ExecutorAllowlistGuardTests
{
    /// <summary>Creates an NTFS junction <paramref name="link"/> → <paramref name="target"/> via <c>cmd /c mklink /J</c> (no privilege required).</summary>
    private static void MakeJunction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(5000);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException("mklink /J failed: " + p.StandardError.ReadToEnd());
        }
    }

    [SkippableFact] // On Windows this always runs — junctions need no privilege; it IS the guard-runs-first proof.
    public async Task MoveToJunctionEscapingAllowedRoot_IsBlocked_NoFolderRowLeaked()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "needs an NTFS junction (cmd /c mklink /J)");

        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // Source folder + file live in a "source" subtree; the allowed root is a SEPARATE subtree.
            string srcDir = Directory.CreateDirectory(Path.Combine(dir.Root, "source")).FullName;
            string allowed = Directory.CreateDirectory(Path.Combine(dir.Root, "allowed")).FullName;
            string outside = Directory.CreateDirectory(Path.Combine(dir.Root, "outside")).FullName;

            // A junction physically INSIDE the allowed root but resolving OUTSIDE it.
            string escape = Path.Combine(allowed, "escape");
            MakeJunction(escape, outside);

            string srcFolderPath = srcDir.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolderPath, "clip.mkv", "My Film");

            string oldFull = Path.Combine(srcDir, "clip.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            // Hand-build a MOVE whose target folder is the junction-escape path.
            string escapeFwd = escape.Replace('\\', '/');
            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, srcFolderPath + "/clip.mkv", escapeFwd + "/My Film.mkv",
                    RenamerStatus.Move, "My Film.mkv", escapeFwd),
            ]);

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var executor = new RenamerExecutor(port, bus, new FakeRevertJournal(), "run-test", new DiskMover());
            var options = new RenamerOptions { AllowedRoots = [allowed.Replace('\\', '/')] };

            int foldersBefore = await db.Set<Folder>().CountAsync();

            var result = await executor.ExecuteAsync(plan, options, default);

            // (a) The item is BLOCKED — SkipBlocked, with the guard's "outside every allowed root" reason.
            var skipped = Assert.Single(result.Skipped);
            Assert.Equal(RenamerStatus.SkipBlocked, skipped.Status);
            Assert.NotNull(skipped.Reason);
            Assert.Contains("outside every allowed root", skipped.Reason);
            Assert.Empty(result.Renamed);
            Assert.Empty(result.Failed);
            Assert.Empty(bus.Published);

            // (b) The physical file did NOT move — it stays at the source.
            Assert.True(File.Exists(oldFull), "blocked move must leave the source file in place");
            Assert.Equal("video-bytes", File.ReadAllText(oldFull));
            Assert.False(File.Exists(Path.Combine(escape, "My Film.mkv")), "no file may land at the escape destination");

            // (c) NO Folder DB row was created for the rejected destination (the guard ran BEFORE
            //     GetOrCreateFolderIdAsync). The folder count is unchanged and no row points at the escape.
            Assert.Equal(foldersBefore, await db.Set<Folder>().CountAsync());
            Assert.False(
                await db.Set<Folder>().AnyAsync(f => f.Path == escapeFwd),
                "no Folder row may be persisted for the out-of-allowlist escape path");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [SkippableFact] // On Windows this always runs — junctions need no privilege; it IS the (4b) re-check proof.
    public async Task LeafSwappedToJunctionBetweenGuards_IsBlocked_NothingEscapes()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "needs an NTFS junction (cmd /c mklink /J)");

        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcDir = Directory.CreateDirectory(Path.Combine(dir.Root, "source")).FullName;
            string allowed = Directory.CreateDirectory(Path.Combine(dir.Root, "allowed")).FullName;
            string outside = Directory.CreateDirectory(Path.Combine(dir.Root, "outside")).FullName;

            // The destination is a REAL, empty directory at check time, so the pre-mutation folder
            // check at (1b) accepts it. It only becomes an escape AFTER that check has passed.
            string dest = Directory.CreateDirectory(Path.Combine(allowed, "dest")).FullName;

            string srcFolderPath = srcDir.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolderPath, "clip.mkv", "My Film");

            string oldFull = Path.Combine(srcDir, "clip.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            string destFwd = dest.Replace('\\', '/');
            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, srcFolderPath + "/clip.mkv", destFwd + "/My Film.mkv",
                    RenamerStatus.Move, "My Film.mkv", destFwd),
            ]);

            // The swap runs inside GetOrCreateFolderIdAsync — the FIRST await after (1b) and the only
            // window that lands between the two guards, so the leaf is real when (1b) reads it and a
            // junction-to-elsewhere when (4b) re-resolves it. Passing NO pre-resolved folder map is what
            // routes the executor through that call at all.
            var port = new SwapLeafToJunctionOnFolderResolve(new CoveRenamerDataPort(db), dest, outside);
            var bus = new CapturingEventBus();
            var executor = new RenamerExecutor(port, bus, new FakeRevertJournal(), "run-test", new DiskMover());
            var options = new RenamerOptions { AllowedRoots = [allowed.Replace('\\', '/')] };

            var result = await executor.ExecuteAsync(plan, options, default);

            // (a) The premise held. A swap that silently failed to happen would make every assert below
            //     pass for the wrong reason — this test would then prove nothing at all.
            Assert.True(port.Swapped, "the check/use swap never fired: GetOrCreateFolderIdAsync was not reached");
            Assert.NotNull(new DirectoryInfo(dest).LinkTarget);

            // (b) The item is BLOCKED by the final-path re-check, with the guard's reason.
            var skipped = Assert.Single(result.Skipped);
            Assert.Equal(RenamerStatus.SkipBlocked, skipped.Status);
            Assert.NotNull(skipped.Reason);
            Assert.Contains("outside every allowed root", skipped.Reason);
            Assert.Empty(result.Renamed);
            Assert.Empty(result.Failed);
            Assert.Empty(bus.Published);

            // (c) Nothing escaped: the source stayed put and no file landed through the junction.
            Assert.True(File.Exists(oldFull), "a blocked move must leave the source file in place");
            Assert.Equal("video-bytes", File.ReadAllText(oldFull));
            Assert.False(File.Exists(Path.Combine(outside, "My Film.mkv")),
                "no file may land outside every allowed root through the swapped leaf");

            // Deliberately NOT asserted: Folder-row absence. The delegated call legitimately persists a
            // row for the destination path, which was still real when (1b) approved it. Row creation is
            // the other pin's subject; asserting it here would make this test fail for the wrong reason.
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task MoveToRealSubdirUnderAllowedRoot_Succeeds()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcDir = Directory.CreateDirectory(Path.Combine(dir.Root, "source")).FullName;
            string allowed = Directory.CreateDirectory(Path.Combine(dir.Root, "allowed")).FullName;
            // A real subdirectory physically under the allowed root (no reparse point).
            string target = Directory.CreateDirectory(Path.Combine(allowed, "season-01")).FullName;

            string srcFolderPath = srcDir.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolderPath, "clip.mkv", "My Film");

            string oldFull = Path.Combine(srcDir, "clip.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            string targetFwd = target.Replace('\\', '/');
            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, srcFolderPath + "/clip.mkv", targetFwd + "/My Film.mkv",
                    RenamerStatus.Move, "My Film.mkv", targetFwd),
            ]);

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var executor = new RenamerExecutor(port, bus, new FakeRevertJournal(), "run-test", new DiskMover());
            var options = new RenamerOptions { AllowedRoots = [allowed.Replace('\\', '/')] };

            var result = await executor.ExecuteAsync(plan, options, default);

            // A benign in-allowlist destination moves: item renamed/moved, file on disk at the new path.
            var moved = Assert.Single(result.Renamed);
            Assert.Equal(RenamerStatus.Move, moved.Status);
            Assert.Empty(result.Skipped);
            Assert.Empty(result.Failed);

            string newFull = Path.Combine(target, "My Film.mkv");
            Assert.True(File.Exists(newFull), "the benign in-allowlist move must land on disk");
            Assert.False(File.Exists(oldFull), "the source must be gone after a successful move");
            Assert.Equal("video-bytes", File.ReadAllText(newFull));

            // The destination folder row is now persisted (the guard accepted it before the create).
            Assert.True(await db.Set<Folder>().AnyAsync(f => f.Path == targetFwd));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// A real <see cref="CoveRenamerDataPort"/> with ONE behavior added: the first
    /// <see cref="GetOrCreateFolderIdAsync"/> call replaces an empty destination directory with a
    /// junction pointing elsewhere, then delegates unchanged.
    /// </summary>
    /// <remarks>
    /// The seam matters more than the mechanism. That call is the first await the executor makes after
    /// the pre-mutation folder check, so a swap performed here is the check/use race the final-path
    /// re-check exists to close, reproduced deterministically rather than by timing. Every other member
    /// forwards verbatim, so the executor is exercised against real data throughout. If the executor is
    /// ever reordered so this call no longer sits between the two guards, <see cref="Swapped"/> stays
    /// false and the test fails loudly instead of passing for a reason it no longer proves.
    /// </remarks>
    private sealed class SwapLeafToJunctionOnFolderResolve(
        IRenamerDataPort inner, string leafToSwap, string junctionTarget) : IRenamerDataPort
    {
        /// <summary>True once the swap has actually been performed.</summary>
        public bool Swapped { get; private set; }

        public Task<int> GetOrCreateFolderIdAsync(string folderPath, CancellationToken ct = default)
        {
            if (!Swapped)
            {
                Directory.Delete(leafToSwap); // empty by construction — no recursive delete
                MakeJunction(leafToSwap, junctionTarget);
                Swapped = true;
            }

            return inner.GetOrCreateFolderIdAsync(folderPath, ct);
        }

        public Task<RenamerEntity?> LoadEntityAsync(RenamerFileKind kind, int entityId, CancellationToken ct = default)
            => inner.LoadEntityAsync(kind, entityId, ct);

        public Task<IReadOnlyList<int>> LoadAllEntityIdsAsync(RenamerFileKind kind, CancellationToken ct = default)
            => inner.LoadAllEntityIdsAsync(kind, ct);

        public Task<IReadOnlyList<int>> LoadEntityIdPageAsync(
            RenamerFileKind kind, int afterEntityId, int take, CancellationToken ct = default)
            => inner.LoadEntityIdPageAsync(kind, afterEntityId, take, ct);

        public Task<IReadOnlyList<RenamerEntity>> LoadEntitiesAsync(
            RenamerFileKind kind, IReadOnlyList<int> ids, CancellationToken ct = default)
            => inner.LoadEntitiesAsync(kind, ids, ct);

        public Task<bool> CollisionExistsAsync(int folderId, string basename, int selfFileId, CancellationToken ct = default)
            => inner.CollisionExistsAsync(folderId, basename, selfFileId, ct);

        public Task<int?> TryGetFolderIdAsync(string folderPath, CancellationToken ct = default)
            => inner.TryGetFolderIdAsync(folderPath, ct);

        public Task<bool> SourceExistsAsync(string fullPath, CancellationToken ct = default)
            => inner.SourceExistsAsync(fullPath, ct);

        public Task<IReadOnlyList<SavedFile>> ApplyAndSaveAsync(
            IReadOnlyList<RenamerFileMutation> mutations, CancellationToken ct = default)
            => inner.ApplyAndSaveAsync(mutations, ct);
    }
}
