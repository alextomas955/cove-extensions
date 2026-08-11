using Cove.Data;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Execution;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Journal;

/// <summary>
/// The revert journal against a real <see cref="CoveContext"/>: a row appends, reads back, and is
/// retired through the port, and the batch aggregate outlives the rows it counted.
/// </summary>
/// <remarks>
/// Driven through the real EF implementation rather than the fake, because the property under test —
/// that what remains in the table IS the work left — is a property of the storage, and a fake that
/// reimplements it would only prove the fake agrees with itself.
/// </remarks>
[Trait("Tier", "L1")]
[Collection(CoveDataExtensionScope.CollectionName)]
public sealed class RevertJournalTests
{
    private static readonly DateTime Opened = new(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task AppendedRows_ReadBackNewestFirst()
    {
        // Newest-first is a correctness requirement, not presentation: one run can rename A→B and then
        // B→C, so reversing in reverse-append order is what frees each slot before the next row needs it.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;
        var journal = await SeedBatchAsync(db, "run-1", 3);

        var batch = await journal.ReadLastOpenBatchAsync();

        Assert.NotNull(batch);
        Assert.Equal("run-1", batch.RunId);
        Assert.Equal(RenamerFileKind.Video, batch.Kind);
        Assert.Equal([3L, 2L, 1L], batch.Rows.Select(r => r.Seq));
        Assert.Equal(["/media/old/3.mkv", "/media/old/2.mkv", "/media/old/1.mkv"], batch.Rows.Select(r => r.OldPath));
    }

    [Fact]
    public async Task RetiringOneRow_LeavesTheOthersPending()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;
        var journal = await SeedBatchAsync(db, "run-1", 3);

        await journal.DeleteRowAsync("run-1", seq: 2, unrestorable: false);

        var batch = await journal.ReadLastOpenBatchAsync();
        Assert.NotNull(batch);
        Assert.Equal([3L, 1L], batch.Rows.Select(r => r.Seq));
    }

    [Fact]
    public async Task WhenEveryRowIsRetired_TheAggregateStillDescribesTheBatch()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;
        var journal = await SeedBatchAsync(db, "run-1", 3);

        for (long seq = 1; seq <= 3; seq++)
        {
            await journal.DeleteRowAsync("run-1", seq, unrestorable: false);
        }

        // Nothing left to offer…
        Assert.Null(await journal.ReadLastOpenBatchAsync());

        // …and yet the panel can still say what the run was: the aggregate outlives its rows.
        var summary = await journal.ReadLastBatchSummaryAsync();
        Assert.NotNull(summary);
        Assert.Equal("run-1", summary.Value.RunId);
        Assert.Equal(Opened.Ticks, summary.Value.WrittenAtUtcTicks);
        Assert.Equal(3, summary.Value.OriginalCount);
        Assert.Equal(3, summary.Value.RestoredCount);
        Assert.Equal(0, summary.Value.Remaining);
    }

    [Fact]
    public async Task TheFlagChoosesWhichCounterMoves_AndTheOriginalCountNeverDoes()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;
        var journal = await SeedBatchAsync(db, "run-1", 3);

        await journal.DeleteRowAsync("run-1", seq: 1, unrestorable: false);
        await journal.DeleteRowAsync("run-1", seq: 2, unrestorable: true);

        var summary = await journal.ReadLastBatchSummaryAsync();
        Assert.NotNull(summary);
        Assert.Equal(3, summary.Value.OriginalCount);
        Assert.Equal(1, summary.Value.RestoredCount);
        Assert.Equal(1, summary.Value.UnrestorableCount);
        Assert.Equal(1, summary.Value.Remaining);
    }

    [Fact]
    public async Task RetiringARowThatIsAlreadyGone_ChangesNothing()
    {
        // An undo can be retried, and a retry re-walks rows it may already have settled.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;
        var journal = await SeedBatchAsync(db, "run-1", 1);

        await journal.DeleteRowAsync("run-1", seq: 1, unrestorable: false);
        await journal.DeleteRowAsync("run-1", seq: 1, unrestorable: false);

        var summary = await journal.ReadLastBatchSummaryAsync();
        Assert.NotNull(summary);
        Assert.Equal(1, summary.Value.RestoredCount);
        Assert.Equal(0, summary.Value.Remaining);
    }

    [Fact]
    public async Task TheNewestBatchIsTheOneOffered_EvenWhileAnOlderOneStillHasRows()
    {
        // The auto-renamer opens its own batch per metadata edit, so several batches with rows left is
        // the ordinary state, not an edge case.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        await SeedBatchAsync(db, "run-old", 2, Opened);
        var journal = await SeedBatchAsync(db, "run-new", 1, Opened.AddMinutes(5));

        var batch = await journal.ReadLastOpenBatchAsync();
        Assert.NotNull(batch);
        Assert.Equal("run-new", batch.RunId);
        Assert.Single(batch.Rows);
    }

    [Fact]
    public async Task ThePurge_RefusesRatherThanReportingASuccessItCannotDeliver()
    {
        // A purge that returns quietly while deleting nothing is indistinguishable from one that
        // works, and what it would hide is the journal growing without a bound.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        var journal = new CoveRevertJournal(db);

        await Assert.ThrowsAsync<NotImplementedException>(() => journal.PurgeExpiredAsync(Opened));
    }

    [Fact]
    public async Task AContextBuiltAfterALateRegistration_ResolvesTheEntitiesItBrought()
    {
        // The measured failure the shared factory's model-cache-key replacement exists to close. EF
        // caches a built model under a key that, by default, says nothing about which data extensions
        // are loaded — so once ANY context has been built, every context after it is handed that same
        // cached model, and an extension registered later has its entity types missing from a model
        // that is never rebuilt. Test classes run in parallel, so which context is built first is not
        // controllable: the failure would come and go rather than fail honestly.
        var (before, beforeConn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = before;
        await using var __ = beforeConn;

        // Building it is the point — this is what populates the cache the next context would inherit.
        Assert.Null(before.Model.FindEntityType(typeof(LateRegistrationProbeEntity)));

        using var registration = CoveDataExtensionScope.WithAdditional(new LateRegistrationProbe());

        var (after, afterConn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var ___ = after;
        await using var ____ = afterConn;

        Assert.NotNull(after.Model.FindEntityType(typeof(LateRegistrationProbeEntity)));
    }

    private static async Task<CoveRevertJournal> SeedBatchAsync(
        CoveContext db, string runId, int rows, DateTime? openedAt = null)
    {
        var journal = new CoveRevertJournal(db);
        await journal.BeginBatchAsync(runId, RenamerFileKind.Video, openedAt ?? Opened);

        for (int i = 1; i <= rows; i++)
        {
            await journal.AppendAsync(
                new RevertRow(runId, Seq: 0, EntityId: 100 + i, FileId: 200 + i, $"/media/old/{i}.mkv", ""));
        }

        return journal;
    }

    /// <summary>An entity type no model has ever seen, so its presence can only come from the registration.</summary>
    private sealed class LateRegistrationProbeEntity
    {
        public int Id { get; set; }
    }

    private sealed class LateRegistrationProbe : IDataExtension
    {
        public string Id => "com.renamer.tests.late-registration-probe";

        public string Name => "Late registration probe";

        public string Version => "1.0.0";

        public string? Description => null;

        public string? Author => null;

        public string? Url => null;

        public string? IconUrl => null;

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
        {
        }

        public void ConfigureModel(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<LateRegistrationProbeEntity>(entity =>
            {
                entity.ToTable("renamer_tests_late_registration_probe");
                entity.HasKey(e => e.Id);
            });
    }
}
