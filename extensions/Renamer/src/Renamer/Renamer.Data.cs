using Microsoft.EntityFrameworkCore;
using Renamer.Execution;

namespace Renamer;

/// <summary>
/// The entity model this extension contributes to the host's context, and the schema migration the
/// host applies for it.
/// </summary>
/// <remarks>
/// The base class already declares the capability interface, so nothing is added to the class
/// declaration here. The member that hands the host its migrations clears the list and calls
/// <c>DefineMigrations</c> every time it is asked, so that override must stay deterministic and free
/// of side effects.
/// </remarks>
public sealed partial class Renamer
{
    public override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RevertBatchEntity>(batch =>
        {
            batch.ToTable(RevertJournalSchema.BatchTable);
            batch.HasKey(b => b.RunId);
            batch.Property(b => b.RunId).HasColumnName("run_id");
            batch.Property(b => b.OpenedAtUtcTicks).HasColumnName("opened_at_utc_ticks");
            batch.Property(b => b.Kind).HasColumnName("kind");
            batch.Property(b => b.OperationId).HasColumnName("operation_id");
            batch.Property(b => b.OriginalCount).HasColumnName("original_count");
            batch.Property(b => b.RestoredCount).HasColumnName("restored_count");
            batch.Property(b => b.UnrestorableCount).HasColumnName("unrestorable_count");
            batch.HasIndex(b => b.OperationId).HasDatabaseName("ix_renamer_revert_batches_operation");
        });

        modelBuilder.Entity<RevertRowEntity>(row =>
        {
            row.ToTable(RevertJournalSchema.RowTable);
            row.HasKey(r => new { r.RunId, r.Seq });
            row.Property(r => r.RunId).HasColumnName("run_id");
            row.Property(r => r.Seq).HasColumnName("seq");
            row.Property(r => r.EntityId).HasColumnName("entity_id");
            row.Property(r => r.FileId).HasColumnName("file_id");
            row.Property(r => r.OldPath).HasColumnName("old_path");
            row.Property(r => r.SidecarsJson).HasColumnName("sidecars_json");
            row.HasIndex(r => r.RunId).HasDatabaseName("ix_renamer_revert_rows_run");
        });
    }

    protected override void DefineMigrations()
    {
        // In order, and each one added rather than edited: the host applies a name once, so the second
        // migration only ever sees a table the first one created.
        Migration(RevertJournalSchema.Migration001Name, RevertJournalSchema.Migration001UpSql);
        Migration(RevertJournalSchema.Migration002Name, RevertJournalSchema.Migration002UpSql);
    }
}
