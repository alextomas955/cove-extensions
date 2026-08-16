using System.Collections.Concurrent;

using Cove.Core.Events;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Renamer.Execution;
using Renamer.Jobs;
using Renamer.Options;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Concurrency;

/// <summary>
/// Parallel-batch correctness under the two-phase rewrite. Proves: every acting item
/// renames and the shared journal holds exactly one well-formed row per success (no torn/lost
/// append under real parallel workers); a per-item fault is an isolated skip while the rest succeed
/// and the batch still reports the final <c>1.0</c> (classify-not-throw under parallelism); a
/// same-volume-only batch runs despite a tiny free-space probe (same-volume is excluded from the
/// free-space sum); and an in-flight free-space drop skips a cross-volume item gracefully. Cove
/// disables EF thread-safety checks, so every assertion is on observable outcomes (files, DB rows,
/// journal rows) — never on an EF exception. The store is a thread-safe
/// <see cref="ConcurrentFakeStore"/> so it is not a confounder.
/// </summary>
[Trait("Tier", "L1")]
public sealed class ParallelBatchTests
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

    /// <summary>Wires the extension over a SCOPED DbContext factory so each worker gets its OWN context over the shared DB.</summary>
    /// <param name="shared">The shared-cache SQLite database every scope opens a context over.</param>
    /// <param name="options">The renamer options saved into the extension's store before initialization.</param>
    /// <param name="logSink">
    /// When supplied, the extension resolves a logger that appends every formatted message here. The
    /// batch's per-item classification (status + reason) reaches no return value and no DB row — the
    /// log line IS the artifact — so a test that asserts WHY an item was skipped has to read it.
    /// Null leaves the extension on its NullLogger default, exactly as the other cases here run.
    /// </param>
    /// <param name="libraryRoots">
    /// Registered as Cove's configured library paths when any are named — the list a destination root is
    /// chosen from and re-checked against. Omitted, no <c>CoveConfiguration</c> is registered at all.
    /// </param>
    private static async Task<(global::Renamer.Renamer ext, ConcurrentFakeStore store, CapturingEventBus bus)>
        BuildAsync(
            SharedCacheSqlite shared,
            RenamerOptions options,
            ConcurrentQueue<string>? logSink = null,
            params string[] libraryRoots)
    {
        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => shared.NewContext());
        var bus = new CapturingEventBus();
        services.AddSingleton<IEventBus>(bus);
        if (logSink is not null)
        {
            services.AddSingleton<ILogger<global::Renamer.Renamer>>(new CapturingLogger<global::Renamer.Renamer>(logSink));
        }

        if (libraryRoots.Length > 0)
        {
            services.AddSingleton(new Cove.Core.Interfaces.CoveConfiguration
            {
                CovePaths = [.. libraryRoots.Select(r => new Cove.Core.Interfaces.CovePath { Path = r })],
            });
        }

        var provider = services.BuildServiceProvider();

        var ext = RenamerFixture.Create();
        var store = new ConcurrentFakeStore();
        await new OptionsStore(store).SaveAsync(options);
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(provider);
        return (ext, store, bus);
    }

    [Fact]
    public async Task ParallelBatch_AllItemsRenamed_RevertLogRowsEqualSuccesses()
    {
        using var dir = new TempDir();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            const int k = 8;
            string folderPath = dir.Root.Replace('\\', '/');
            await using var seedDb = shared.NewContext();

            var (folderId, firstVideo, _) =
                await ExecutorTestSeed.SeedVideoAsync(seedDb, folderPath, "raw 0.mkv", "Film 0");
            var ids = new List<int> { firstVideo };
            File.WriteAllText(Path.Combine(dir.Root, "raw 0.mkv"), "bytes-0");
            for (int i = 1; i < k; i++)
            {
                var video = new Cove.Core.Entities.Video { Title = $"Film {i}", Organized = true };
                seedDb.Set<Cove.Core.Entities.Video>().Add(video);
                await seedDb.SaveChangesAsync();
                await ExecutorTestSeed.SeedAdditionalFileAsync(seedDb, folderId, video.Id, $"raw {i}.mkv");
                ids.Add(video.Id);
                File.WriteAllText(Path.Combine(dir.Root, $"raw {i}.mkv"), $"bytes-{i}");
            }

            var (ext, _, _) = await BuildAsync(shared, new RenamerOptions { FilenameTemplate = "$title" });
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", ids), progress, default);

            // All K renamed on disk.
            for (int i = 0; i < k; i++)
            {
                Assert.True(File.Exists(Path.Combine(dir.Root, $"Film {i}.mkv")), $"Film {i}.mkv missing");
                Assert.False(File.Exists(Path.Combine(dir.Root, $"raw {i}.mkv")), $"raw {i}.mkv lingered");
            }

            // The shared journal (read fresh over its own context) holds exactly K well-formed rows,
            // each with the distinct sequence number that half-identifies it — a lost or torn append
            // under real parallel workers would show up as a short count or a repeated key.
            await using var readDb = shared.NewContext();
            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(new CoveRevertJournal(readDb));
            Assert.NotNull(batch);
            Assert.Equal(k, batch!.Rows.Count);
            Assert.Equal(k, batch.Rows.Select(e => e.FileId).Distinct().Count());
            Assert.Equal(k, batch.Rows.Select(e => e.Seq).Distinct().Count());
            Assert.All(batch.Rows, e =>
            {
                Assert.NotEqual(0, e.FileId);
                Assert.False(string.IsNullOrEmpty(e.OldPath));
            });

            Assert.Equal(1d, progress.LastPercent);

            // Progress must move during BOTH phases, not jump from 0% to done. PHASE A (planning) drives
            // the bar into (0, 0.5] and PHASE B (executing) carries it past 0.5 to 1.0 — so there must be
            // at least one report in each band, every report is in [0,1], and the sequence never regresses.
            Assert.Contains(progress.Reports, r => r.Percent is > 0d and <= 0.5d);
            Assert.Contains(progress.Reports, r => r.Percent is > 0.5d and < 1d);
            Assert.All(progress.Reports, r => Assert.InRange(r.Percent, 0d, 1d));
            var seq = progress.Reports.Select(r => r.Percent).ToList();
            Assert.Equal(seq.OrderBy(p => p).ToList(), seq);
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    [Fact]
    public async Task ParallelBatch_OneItemFaults_OthersSucceed_BatchCompletes()
    {
        using var dir = new TempDir();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            const int k = 6;
            const int faultIndex = 3; // this id's on-disk source is intentionally absent.
            string folderPath = dir.Root.Replace('\\', '/');
            await using var seedDb = shared.NewContext();

            var (folderId, firstVideo, _) =
                await ExecutorTestSeed.SeedVideoAsync(seedDb, folderPath, "raw 0.mkv", "Film 0");
            var ids = new List<int> { firstVideo };
            File.WriteAllText(Path.Combine(dir.Root, "raw 0.mkv"), "bytes-0");
            for (int i = 1; i < k; i++)
            {
                var video = new Cove.Core.Entities.Video { Title = $"Film {i}", Organized = true };
                seedDb.Set<Cove.Core.Entities.Video>().Add(video);
                await seedDb.SaveChangesAsync();
                await ExecutorTestSeed.SeedAdditionalFileAsync(seedDb, folderId, video.Id, $"raw {i}.mkv");
                ids.Add(video.Id);
                // Write the on-disk source for every id EXCEPT the fault one — with no source on disk
                // the executor's source pre-check classifies it as SkipMissingSource (not a mover-level
                // lock skip) without throwing, so the batch still completes.
                if (i != faultIndex)
                {
                    File.WriteAllText(Path.Combine(dir.Root, $"raw {i}.mkv"), $"bytes-{i}");
                }
            }

            var (ext, _, _) = await BuildAsync(shared, new RenamerOptions { FilenameTemplate = "$title" });
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", ids), progress, default);

            // Every item whose source existed renamed; the faulting item did NOT (its target was never
            // created) and the batch still finished at 1.0 — one bad item never aborts the run.
            for (int i = 0; i < k; i++)
            {
                if (i == faultIndex)
                {
                    Assert.False(File.Exists(Path.Combine(dir.Root, $"Film {i}.mkv")),
                        "the faulting item must not have produced a renamed file");
                }
                else
                {
                    Assert.True(File.Exists(Path.Combine(dir.Root, $"Film {i}.mkv")), $"Film {i}.mkv missing");
                }
            }

            Assert.Equal(1d, progress.LastPercent);
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    [Fact]
    public async Task SameVolumeBatch_NotThrottled_AndExcludedFromFreeSpace()
    {
        using var dir = new TempDir();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            const int k = 5;
            string folderPath = dir.Root.Replace('\\', '/');
            await using var seedDb = shared.NewContext();

            var (folderId, firstVideo, _) =
                await ExecutorTestSeed.SeedVideoAsync(seedDb, folderPath, "raw 0.mkv", "Film 0");
            var ids = new List<int> { firstVideo };
            File.WriteAllText(Path.Combine(dir.Root, "raw 0.mkv"), "bytes-0");
            for (int i = 1; i < k; i++)
            {
                var video = new Cove.Core.Entities.Video { Title = $"Film {i}", Organized = true };
                seedDb.Set<Cove.Core.Entities.Video>().Add(video);
                await seedDb.SaveChangesAsync();
                await ExecutorTestSeed.SeedAdditionalFileAsync(seedDb, folderId, video.Id, $"raw {i}.mkv");
                ids.Add(video.Id);
                File.WriteAllText(Path.Combine(dir.Root, $"raw {i}.mkv"), $"bytes-{i}");
            }

            // CrossVolumeConcurrency = 1 would throttle a cross-volume group, but same-volume runs under
            // the unthrottled group regardless; the TINY probe (1 byte free everywhere) must NOT refuse
            // the batch because same-volume moves are excluded from the free-space sum.
            var (ext, _, _) = await BuildAsync(shared,
                new RenamerOptions { FilenameTemplate = "$title", CrossVolumeConcurrency = 1 });
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", ids), progress, default,
                freeSpaceProbe: _ => 1L);

            for (int i = 0; i < k; i++)
            {
                Assert.True(File.Exists(Path.Combine(dir.Root, $"Film {i}.mkv")), $"Film {i}.mkv missing");
            }
            Assert.Equal(1d, progress.LastPercent);
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task InFlightFreeSpaceDrop_SkipsCrossVolumeItemGracefully()
    {
        Skip.IfNot(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        using var dir = new TempDir();
        using var drive = new SecondVolume(); // a distinct path root that backs the same physical volume.
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            string srcFolder = Path.Combine(dir.Root, "incoming");
            Directory.CreateDirectory(srcFolder);
            string srcPathFwd = srcFolder.Replace('\\', '/');
            string destRootFwd = drive.Root.Replace('\\', '/'); // e.g. "P:/"

            await using var seedDb = shared.NewContext();
            var (_, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(seedDb, srcPathFwd, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(srcFolder, "raw.mkv"), "bytes");

            // The guard's Needed is the DB's RECORDED size, never the file on disk, and a seeded row
            // defaults to Size 0 - which makes Needed 0 and `Needed > Available` unsatisfiable for any
            // probe value whatsoever. So without a real size here the in-flight check is a no-op and
            // this test proves nothing. Measured, which is how it was found: it passed on Windows only
            // because the destination was refused for an unrelated reason, and the moment it ran on
            // Linux the file moved. A test whose premise cannot hold is worse than no test.
            var fileRow = await seedDb.Set<Cove.Core.Entities.VideoFile>().FirstAsync(f => f.Id == fileId);
            fileRow.Size = 4096;
            await seedDb.SaveChangesAsync();

            // Route the item across volumes (src on the temp drive → dest on the subst drive root), so
            // the partition classifies it cross-volume and the worker runs the in-flight Shortfall.
            var options = new RenamerOptions
            {
                FilenameTemplate = "$title",
                AllowedRoots = [srcPathFwd, destRootFwd],
                PathDestinations =
                    [new PathDestinationRule
                    {
                        Pattern = srcPathFwd, Dest = Dests.At(destRootFwd, "Films"), IsRegex = false,
                    }],
                FreeSpaceHeadroomBytes = 0,
            };
            var (ext, _, _) = await BuildAsync(shared, options, libraryRoots: destRootFwd);

            // Stateful TOCTOU probe: the FIRST reading (PHASE A up-front check) reports ample free space
            // so the batch is accepted; the SECOND reading (PHASE B in-flight re-check, just before the
            // copy) reports near-zero, modelling a concurrent scanner that filled the destination. The
            // cross-volume item must then be skipped gracefully — never thrown, batch still completes.
            int calls = 0;
            long Probe(string vol) => Interlocked.Increment(ref calls) == 1 ? 1L << 40 : 1L;

            var progress = new FakeJobProgress();
            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", [videoId]), progress, default, Probe);

            // The in-flight drop skipped the move: the file stayed at its source and never landed on the
            // routed destination. The batch finished cleanly (no throw, final 1.0).
            Assert.True(File.Exists(Path.Combine(srcFolder, "raw.mkv")),
                "the source must stay put when the in-flight free-space check skips the move");
            Assert.False(File.Exists(Path.Combine(drive.Root, "Films", "My Film.mkv")),
                "no file must land on the destination after an in-flight free-space skip");
            Assert.Equal(1d, progress.LastPercent);
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task StarvedCrossVolumeBatch_IsRefusedUpFront_NoBatchOpens()
    {
        Skip.IfNot(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        using var dir = new TempDir();
        using var drive = new SecondVolume();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            string srcFolder = Path.Combine(dir.Root, "incoming");
            Directory.CreateDirectory(srcFolder);
            string srcPathFwd = srcFolder.Replace('\\', '/');
            string destRootFwd = drive.Root.Replace('\\', '/');

            await using var seedDb = shared.NewContext();
            var (_, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(seedDb, srcPathFwd, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(srcFolder, "raw.mkv"), "bytes");

            // Load-bearing, for the reason spelled out on the in-flight test above: the guard's Needed
            // comes from the RECORDED size, so a default-zero row makes the shortfall unsatisfiable for
            // any probe whatsoever and this test would pass while proving nothing.
            var fileRow = await seedDb.Set<Cove.Core.Entities.VideoFile>().FirstAsync(f => f.Id == fileId);
            fileRow.Size = 4096;
            await seedDb.SaveChangesAsync();

            var options = new RenamerOptions
            {
                FilenameTemplate = "$title",
                AllowedRoots = [srcPathFwd, destRootFwd],
                PathDestinations =
                    [new PathDestinationRule
                    {
                        Pattern = srcPathFwd, Dest = Dests.At(destRootFwd, "Films"), IsRegex = false,
                    }],
                // Zero headroom, so the refusal comes from the shortfall itself rather than from
                // headroom arithmetic that would refuse even an ample volume.
                FreeSpaceHeadroomBytes = 0,
            };
            var (ext, _, _) = await BuildAsync(shared, options, libraryRoots: destRootFwd);

            var progress = new FakeJobProgress();
            // CONSTANT starvation, unlike the in-flight test's stateful probe: every reading reports one
            // byte free, so the destination volume is already too small when the batch is first sized up.
            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", [videoId]), progress, default, _ => 1L);

            // (a) The batch was refused, and said so. Nothing else in the suite reads this message, so a
            //     refusal that stopped reporting would go unnoticed everywhere but here.
            Assert.Equal(1d, progress.LastPercent);
            Assert.StartsWith("Refused: insufficient free space", progress.Reports[^1].Message);

            // (b) No batch was opened. This is the assert that carries the test: the refusal runs before
            //     the journal opens its batch, so a refused run must leave the batches table untouched.
            //     Read the table directly over a fresh context — the undo-target reader cannot stand in
            //     for it, because a batch that opened and then had every item skipped holds no rows and
            //     is not offered as an undo target, which is exactly the state a lost refusal produces.
            await using var readDb = shared.NewContext();
            Assert.Equal(0, await readDb.Set<RevertBatchEntity>().AsNoTracking().CountAsync());

            // Supporting only. Both of these stay green when the up-front refusal is removed, because the
            // in-flight re-check then stops the same item one layer later — so neither one can tell a
            // refused batch from a batch that opened and skipped everything.
            Assert.True(File.Exists(Path.Combine(srcFolder, "raw.mkv")));
            Assert.False(File.Exists(Path.Combine(drive.Root, "Films", "My Film.mkv")));

            // Deliberately NOT asserted: the absence of destination Folder rows. The batch pre-creates
            // every distinct destination folder before it sizes the run up, so a refused batch leaves
            // those rows behind by design.
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task BatchDestinationEscapingAllowedRoot_IsSkipBlocked_NoFolderRowLeaked()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "needs an NTFS junction (cmd /c mklink /J)");

        using var dir = new TempDir();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            string srcFolder = Path.Combine(dir.Root, "incoming");
            Directory.CreateDirectory(srcFolder);
            string library = Directory.CreateDirectory(Path.Combine(dir.Root, "library")).FullName;
            string outside = Directory.CreateDirectory(Path.Combine(dir.Root, "outside")).FullName;

            string srcPathFwd = srcFolder.Replace('\\', '/');
            string libraryFwd = library.Replace('\\', '/');

            // The routed destination is a junction physically INSIDE the allowed root that resolves
            // OUTSIDE it. The pure string gate cannot see that; only the canonical (disk-reading) guard
            // can — and the batch's destination pre-create is the first thing that touches it.
            string escape = Path.Combine(library, "Films");
            MakeJunction(escape, outside);
            string escapeFwd = escape.Replace('\\', '/');

            await using var seedDb = shared.NewContext();
            var (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(seedDb, srcPathFwd, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(srcFolder, "raw.mkv"), "bytes");

            var options = new RenamerOptions
            {
                FilenameTemplate = "$title",
                AllowedRoots = [srcPathFwd, libraryFwd],
                PathDestinations =
                    [new PathDestinationRule
                    {
                        Pattern = srcPathFwd, Dest = Dests.At(libraryFwd, "Films"), IsRegex = false,
                    }],
            };

            // Source and destination share a path root, so the free-space guard excludes this move and
            // the real DriveInfo probe cannot refuse the batch — the ONLY refusal on this run is the
            // allowlist one under test.
            var log = new ConcurrentQueue<string>();
            var (ext, _, _) = await BuildAsync(shared, options, log, libraryFwd);

            var progress = new FakeJobProgress();
            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", [videoId]), progress, default);

            // (a) The item is classified SkipBlocked and carries the GUARD's own reason, not a generic
            //     one — a skip nobody can attribute is a skip nobody can act on. The batch itself
            //     completes: a rejected destination is classified, never thrown, at this boundary.
            Assert.Contains(log, m =>
                m.Contains("skipped (SkipBlocked)", StringComparison.Ordinal)
                && m.Contains("outside every allowed root", StringComparison.Ordinal));
            Assert.Equal(1d, progress.LastPercent);

            // (b) Nothing escaped: the source stayed put and no file landed through the junction.
            Assert.True(File.Exists(Path.Combine(srcFolder, "raw.mkv")),
                "a blocked destination must leave the source file in place");
            Assert.False(File.Exists(Path.Combine(outside, "My Film.mkv")),
                "no file may land outside every allowed root through the junctioned destination");

            // (c) The assert that EARNS this test, and the only one that was red before the batch's
            //     destination pre-create was guarded. The worker already blocked the disk move before
            //     that change — so every assert above passed while the pre-create had ALREADY persisted
            //     a Folder row pointing outside the allowlist. A durable escape artifact with no file
            //     behind it is still an escape artifact, and the executor-path pin
            //     (MoveToJunctionEscapingAllowedRoot_IsBlocked_NoFolderRowLeaked) cannot see it: that
            //     one drives the executor's own resolve, this one drives the batch pre-create.
            await using var readDb = shared.NewContext();
            Assert.False(
                await readDb.Set<Cove.Core.Entities.Folder>().AsNoTracking().AnyAsync(f => f.Path == escapeFwd),
                "no Folder row may be persisted for the out-of-allowlist escape destination");
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    /// <summary>
    /// An <see cref="ILogger{TCategoryName}"/> that appends every formatted message to a shared queue.
    /// </summary>
    /// <remarks>
    /// The queue is concurrent because PHASE B logs from many workers at once, so a plain list would be
    /// a race inside the test harness itself — the one confounder a concurrency suite must not add.
    /// </remarks>
    private sealed class CapturingLogger<T>(ConcurrentQueue<string> sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => sink.Enqueue(formatter(state, exception));
    }
}
