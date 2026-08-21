using Microsoft.EntityFrameworkCore;
using Renamer.Execution;

namespace Renamer;

/// <summary>
/// The extension's data-extension hooks: the entity model it contributes to the host's context, and
/// the schema migration the host applies for it.
///
/// Kept apart from the rest of the extension for the same reason the logging partial is: this is a
/// host capability surface, defined once and never called from extension code, so putting it beside
/// the batch and endpoint logic would only make both harder to read.
/// </summary>
/// <remarks>
/// Nothing is added to the class declaration for this. The base class already declares the capability
/// interface, and the member that hands the host its migrations is not virtual — it clears the list
/// and calls <c>DefineMigrations</c> every time it is asked, so overriding these two is the whole
/// contract, and <c>DefineMigrations</c> must stay deterministic and free of side effects.
/// </remarks>
public sealed partial class Renamer
{
    public override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RevertBatchEntity>(batch =>
        {
            batch.ToTable("renamer_revert_batches");
            batch.HasKey(b => b.RunId);
            batch.Property(b => b.RunId).HasColumnName("run_id");
            batch.Property(b => b.OpenedAtUtcTicks).HasColumnName("opened_at_utc_ticks");
            batch.Property(b => b.Kind).HasColumnName("kind");
            batch.Property(b => b.OriginalCount).HasColumnName("original_count");
            batch.Property(b => b.RestoredCount).HasColumnName("restored_count");
            batch.Property(b => b.UnrestorableCount).HasColumnName("unrestorable_count");
        });

        modelBuilder.Entity<RevertRowEntity>(row =>
        {
            row.ToTable("renamer_revert_rows");
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

    protected override void DefineMigrations() =>
        Migration(RevertJournalSchema.Migration001Name, RevertJournalSchema.Migration001UpSql);
}
