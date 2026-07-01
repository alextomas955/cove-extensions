using Renamer.Execution;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// The UNDO-03 restore-target RE-GATE proofs — the undo-direction mirror of
/// <see cref="ExecutorAllowlistGuardTests"/> + <see cref="CanonicalGuardJunctionTests"/>. The forward
/// write boundary re-validates the destination folder through <see cref="CanonicalPathGuard.Check"/>
/// before it touches disk; an UNDO is ALSO a write (NEW→OLD), so the restore target must pass the SAME
/// gate. The drive layout or the OLD folder may have changed since the move (the allowlist was edited,
/// or the OLD dir is now a junction-to-elsewhere), so a restore that would land OUTSIDE the configured
/// roots is a reported SKIP, never a clobber.
///
/// (a) <see cref="ReGate_RestoreOutsideAllowlist_ReportedSkip"/> — the entry's OLD dir resolves OUTSIDE
/// the configured allowlist (a junction inside an allowed
/// root pointing outside it). The entry lands in Skipped with an allowlist-rejection reason and the
/// file is NOT moved (stays at NEW). (b) <see cref="ReGate_EmptyAllowlist_NoOp"/> — with an EMPTY
/// allowlist (v1.3 legacy) the re-gate is a no-op and a normal same-folder undo proceeds (Undone),
/// keeping UNDO-01 legacy undo byte-identical.
///
/// SQLite (not EF-InMemory) so the unique index + Path recompute are faithful. The junction is created
/// via <c>cmd /c mklink /J</c> (no privilege), so the reject case is a non-skippable
/// <see cref="FactAttribute"/>.
/// </summary>
[Trait("Tier", "Integration")]
[Trait("Adversarial", "Junction")]
public sealed class UndoReGateTests
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

    [Fact] // NOT skippable — junctions always work; this IS the wired-in UNDO-03 re-gate proof.
    public async Task ReGate_RestoreOutsideAllowlist_ReportedSkip()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // The allowed root is one subtree; "outside" is a separate subtree. A junction physically
            // INSIDE the allowed root but resolving OUTSIDE it is the OLD (restore-target) directory.
            string allowed = Directory.CreateDirectory(Path.Combine(dir.Root, "allowed")).FullName;
            string outside = Directory.CreateDirectory(Path.Combine(dir.Root, "outside")).FullName;
            string escape = Path.Combine(allowed, "escape"); // lives inside allowed…
            MakeJunction(escape, outside);                    // …but resolves outside it.

            // The file CURRENTLY lives at NEW (a real folder under the allowed root); the recorded OLD
            // path is the junction-escape dir that resolves outside the allowlist.
            string newDir = Directory.CreateDirectory(Path.Combine(allowed, "current")).FullName;
            string newFull = Path.Combine(newDir, "My Film.mkv");
            string oldFull = Path.Combine(escape, "raw.mkv");
            File.WriteAllText(newFull, "video-bytes");
            Assert.False(File.Exists(oldFull));

            string newFolder = newDir.Replace('\\', '/');
            string escapeFwd = escape.Replace('\\', '/');

            // Seed the file at its CURRENT (NEW) location so the DB is consistent.
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, newFolder, "My Film.mkv", "My Film");

            var entry = new RevertLog.RevertEntry(videoId, fileId, escapeFwd + "/raw.mkv", newFull.Replace('\\', '/'));
            var batch = new RevertLog.RevertBatch(RenamerFileKind.Video, [entry]);

            var port = new CoveRenamerDataPort(db);
            var undoBus = new CapturingEventBus();
            // The allowlist is configured → the re-gate runs. The OLD dir resolves outside it → reject.
            var replayer = new UndoReplayer(port, undoBus, new DiskMover(),
                cross: new CrossVolumeMover(), allowedRoots: [allowed.Replace('\\', '/')]);

            var result = await replayer.RevertAsync(batch, default);

            // The entry is a reported SKIP citing the allowlist rejection — never Undone, never a clobber.
            Assert.Equal(0, result.Undone);
            Assert.Empty(result.Failed);
            var skip = Assert.Single(result.Skipped);
            Assert.NotNull(skip.Reason);
            Assert.Contains("allowlist", skip.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(undoBus.Published);

            // The file did NOT move — it stays at NEW; the escape destination holds nothing.
            Assert.True(File.Exists(newFull), "a re-gated restore must leave the file at NEW");
            Assert.Equal("video-bytes", File.ReadAllText(newFull));
            Assert.False(File.Exists(oldFull), "no file may land at the out-of-allowlist restore target");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReGate_EmptyAllowlist_NoOp()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "My Film.mkv", "My Film");

            // The file currently sits at NEW; a normal same-folder undo restores it to OLD in the same dir.
            string oldFull = Path.Combine(dir.Root, "raw.mkv");
            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            File.WriteAllText(newFull, "video-bytes");

            var entry = new RevertLog.RevertEntry(videoId, fileId, oldFull.Replace('\\', '/'), newFull.Replace('\\', '/'));
            var batch = new RevertLog.RevertBatch(RenamerFileKind.Video, [entry]);

            var port = new CoveRenamerDataPort(db);
            var undoBus = new CapturingEventBus();
            // EMPTY allowlist (the v1.3 legacy default) → the re-gate is a no-op and the undo proceeds.
            var replayer = new UndoReplayer(port, undoBus, new DiskMover(),
                cross: new CrossVolumeMover(), allowedRoots: []);

            var result = await replayer.RevertAsync(batch, default);

            Assert.Equal(1, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);

            // Disk restored to OLD; NEW gone (the legacy same-drive path is unaffected by the empty re-gate).
            Assert.True(File.Exists(oldFull), "an empty-allowlist undo proceeds normally");
            Assert.False(File.Exists(newFull));
            Assert.Equal("video-bytes", File.ReadAllText(oldFull));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// CR-01 regression: a CONFIGURED allowlist must NOT make an IN-PLACE renamer non-undoable when the
    /// file's folder is OUTSIDE the allowlist. The forward executor never gates in-place renames
    /// (`isMove` only), and AllowedRoots is an opt-in widening for relocation DESTINATIONS — not a
    /// confinement of the library's existing folders. An in-place restore (OLD and NEW share the same
    /// directory) crosses no write boundary, so the re-gate MUST be exempted. Before the fix this entry
    /// was Skipped ("restore target rejected by allowlist"), permanently disabling undo. After the fix it
    /// is Undone. The junction-escape RELOCATION case (ReGate_RestoreOutsideAllowlist_ReportedSkip) and
    /// the empty-allowlist no-op stay green — only the in-place case is exempted.
    /// </summary>
    [Fact]
    public async Task ReGate_ConfiguredAllowlist_InPlaceRenamerOutsideAllowlist_Undone()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // The allowlist points at a SEPARATE subtree. The file lives in a sibling "library" folder
            // that is OUTSIDE the allowlist — an ordinary in-place renamer that the forward path never gated.
            string allowed = Directory.CreateDirectory(Path.Combine(dir.Root, "allowed")).FullName;
            string library = Directory.CreateDirectory(Path.Combine(dir.Root, "library")).FullName;

            string folderPath = library.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "My Film.mkv", "My Film");

            // OLD and NEW are both in the SAME (outside-the-allowlist) library folder → an in-place renamer.
            string oldFull = Path.Combine(library, "raw.mkv");
            string newFull = Path.Combine(library, "My Film.mkv");
            File.WriteAllText(newFull, "video-bytes");

            var entry = new RevertLog.RevertEntry(videoId, fileId, oldFull.Replace('\\', '/'), newFull.Replace('\\', '/'));
            var batch = new RevertLog.RevertBatch(RenamerFileKind.Video, [entry]);

            var port = new CoveRenamerDataPort(db);
            var undoBus = new CapturingEventBus();
            // A CONFIGURED allowlist (the regressing precondition) that does NOT cover the library folder.
            var replayer = new UndoReplayer(port, undoBus, new DiskMover(),
                cross: new CrossVolumeMover(), allowedRoots: [allowed.Replace('\\', '/')]);

            var result = await replayer.RevertAsync(batch, default);

            // The in-place restore is exempt from the re-gate → Undone, NOT Skipped.
            Assert.Equal(1, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);
            Assert.Single(undoBus.Published);

            // Disk restored to OLD; NEW gone — the in-place undo proceeded despite the configured allowlist.
            Assert.True(File.Exists(oldFull), "a configured allowlist must not block an in-place undo");
            Assert.False(File.Exists(newFull));
            Assert.Equal("video-bytes", File.ReadAllText(oldFull));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
