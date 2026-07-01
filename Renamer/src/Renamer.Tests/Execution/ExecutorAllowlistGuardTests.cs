using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

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
/// junction is created via <c>cmd /c mklink /J</c> (no privilege), so the reject case is a
/// non-skippable <see cref="FactAttribute"/>.
/// </summary>
[Trait("Tier", "Integration")]
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

    [Fact] // NOT skippable — junctions always work; this IS the wired-in full-path/guard-runs-first proof.
    public async Task MoveToJunctionEscapingAllowedRoot_IsBlocked_NoFolderRowLeaked()
    {
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
            var executor = new RenamerExecutor(port, bus, new RevertLog(new FakeStore()), new DiskMover());
            var options = new RenamerOptions { AllowedRoots = [allowed.Replace('\\', '/')] };

            int foldersBefore = await db.Set<Folder>().CountAsync();

            var result = await executor.ExecuteAsync(plan, options, default);

            // (a) The item is BLOCKED — SkipBlocked, with the guard's "outside every allowed root" reason.
            var skipped = Assert.Single(result.Skipped);
            Assert.Equal(RenamerStatus.SkipBlocked, skipped.Status);
            Assert.NotNull(skipped.Reason);
            Assert.Contains("outside every allowed root", skipped.Reason);
            Assert.Empty(result.Renamerd);
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
            var executor = new RenamerExecutor(port, bus, new RevertLog(new FakeStore()), new DiskMover());
            var options = new RenamerOptions { AllowedRoots = [allowed.Replace('\\', '/')] };

            var result = await executor.ExecuteAsync(plan, options, default);

            // A benign in-allowlist destination moves: item renamerd/moved, file on disk at the new path.
            var moved = Assert.Single(result.Renamerd);
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
}
