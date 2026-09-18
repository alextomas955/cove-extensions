namespace Renamer.Execution;

// The EF shapes behind IRevertJournal, mapped to the extension-owned tables in Renamer.Data.cs and
// created by RevertJournalSchema's migration. These are storage types: the rest of the extension
// speaks in the port's records.
//
// Mutable properties with EF-friendly defaults, because the change tracker sets them on
// materialization and the counter updates are in-place writes on a tracked entity.
public sealed class RevertBatchEntity
{
    public string RunId { get; set; } = "";

    // Ticks in an integer column, because the same DDL has to run on the provider production uses and
    // on the one the tests use.
    public long OpenedAtUtcTicks { get; set; }

    // The run's RenamerFileKind by name.
    public string Kind { get; set; } = "";

    // The user action this batch belongs to; several batches can share one. Empty on every batch
    // written before the column existed, which a reader resolves to that batch's own RunId.
    public string OperationId { get; set; } = "";

    public int OriginalCount { get; set; }

    public int RestoredCount { get; set; }

    public int UnrestorableCount { get; set; }
}

// One pending restore. Its identity is (RunId, Seq).
public sealed class RevertRowEntity
{
    public string RunId { get; set; } = "";

    public long Seq { get; set; }

    public int EntityId { get; set; }

    public int FileId { get; set; }

    public string OldPath { get; set; } = "";

    public string SidecarsJson { get; set; } = "";
}

// The revert journal's physical schema: the migration the host applies, exactly as it ships. The
// host executes this SQL, receipts it by name, and never re-runs a name it has already receipted.
public static class RevertJournalSchema
{
    // The migration name is frozen, and so is its SQL. The host receipts a migration by name and skips
    // a name it has already applied, whatever the content now says, so an edit here would reach a
    // fresh install and never reach an existing one. A schema change is a new constant with a new name.
    public const string Migration001Name = "001_create_revert_journal";

    // Every statement is create-if-absent, because the tables can outlive their receipt in both
    // directions: an uninstall deletes the extension's directory and leaves the receipt, and a restored
    // database can carry the tables with no receipt. A failure here would only be a host log line.
    //
    // The row sequence is minted by the extension, because an auto-numbering column is spelled
    // differently on every provider. old_path is in no key and no index: it is the one column whose
    // length a user controls, so its length can never hit a key limit.
    public const string Migration001UpSql =
        """
        CREATE TABLE IF NOT EXISTS renamer_revert_batches (
            run_id              TEXT    NOT NULL,
            opened_at_utc_ticks BIGINT  NOT NULL,
            kind                TEXT    NOT NULL,
            original_count      INTEGER NOT NULL DEFAULT 0,
            restored_count      INTEGER NOT NULL DEFAULT 0,
            unrestorable_count  INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (run_id)
        );
        CREATE TABLE IF NOT EXISTS renamer_revert_rows (
            run_id        TEXT    NOT NULL,
            seq           BIGINT  NOT NULL,
            entity_id     INTEGER NOT NULL,
            file_id       INTEGER NOT NULL,
            old_path      TEXT    NOT NULL,
            sidecars_json TEXT    NOT NULL DEFAULT '',
            PRIMARY KEY (run_id, seq)
        );
        CREATE INDEX IF NOT EXISTS ix_renamer_revert_rows_run ON renamer_revert_rows (run_id);
        """;

    // Frozen on the same terms as Migration001Name.
    public const string Migration002Name = "002_add_operation_id";

    // Adds the column that groups a user action's batches. One click over several media kinds opens one
    // batch per kind, and undo has to reach all of them or none.
    //
    // Create-if-absent for the reason Migration001UpSql gives. The host stops applying an extension's
    // remaining migrations after one failure, so a single unrunnable statement would block every later
    // migration on that database, on every start.
    //
    // PostgreSQL syntax, which is the only dialect the host runs. SQLite has no
    // ADD COLUMN IF NOT EXISTS, and the tests build their journal schema without executing this
    // string. An existing row
    // takes '', which readers resolve to that batch's own run id.
    public const string Migration002UpSql =
        """
        ALTER TABLE renamer_revert_batches ADD COLUMN IF NOT EXISTS operation_id TEXT NOT NULL DEFAULT '';
        CREATE INDEX IF NOT EXISTS ix_renamer_revert_batches_operation
            ON renamer_revert_batches (operation_id);
        """;
}
