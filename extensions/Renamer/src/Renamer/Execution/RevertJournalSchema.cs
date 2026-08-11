namespace Renamer.Execution;

/// <summary>
/// The revert journal's physical schema: the migration the host applies, exactly as it ships.
///
/// The host — not this extension — executes this SQL, receipts it by name, and never re-runs a name
/// it has already receipted. Everything about the statements below follows from that plus one more
/// constraint: the same string has to run on the database production uses and on the one the tests
/// use, so nothing provider-specific may appear in it.
/// </summary>
public static class RevertJournalSchema
{
    /// <summary>The migration's name, which is FROZEN: it must never change, and neither must its SQL.</summary>
    /// <remarks>
    /// The host receipts a migration by NAME and skips any name it has already applied, whatever the
    /// content now says. So editing this migration's statements later would reach a fresh install and
    /// never reach an existing one, and the two populations would diverge with nothing to notice.
    /// A schema change is therefore a NEW constant with a NEW name, added beside this one — never an
    /// edit to it.
    /// </remarks>
    public const string Migration001Name = "001_create_revert_journal";

    /// <summary>
    /// The statements that create the journal. Every one is create-if-absent, because the table can
    /// outlive its receipt in both directions: an uninstall deletes the extension's directory and
    /// nothing deletes the receipt, while a restored database can carry the tables with no receipt at
    /// all. Re-running has to be harmless, and a failure here would only be a host log line.
    /// </summary>
    /// <remarks>
    /// Two choices in here are load-bearing and easy to undo by accident.
    /// <para>
    /// The row sequence is minted by the extension, never by the database. An auto-numbering column
    /// is spelled differently on every provider, and taking one would cost the whole tier of tests
    /// that runs these exact statements.
    /// </para>
    /// <para>
    /// <c>old_path</c> is in no key and no index. It is the one column whose length a user controls,
    /// and keeping it out of every key means its length can never be a limit.
    /// </para>
    /// </remarks>
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
}
