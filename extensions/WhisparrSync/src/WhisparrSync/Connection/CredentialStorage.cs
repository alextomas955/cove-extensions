namespace WhisparrSync.Connection;

/// <summary>
/// The stored API key for one Whisparr generation, mapped to the extension-owned table in
/// <c>WhisparrSync.Data.cs</c> and created by <see cref="WhisparrCredentialSchema"/>'s migration.
/// </summary>
/// <remarks>
/// A storage type, not a domain model: nothing outside <c>CredentialPort</c> and the model
/// configuration should name it.
/// <para>
/// Mutable properties with EF-friendly defaults rather than a record, because the change tracker
/// sets them on materialization and a replacement is an in-place write on a tracked entity.
/// </para>
/// </remarks>
public sealed class WhisparrCredentialEntity
{
    /// <summary>
    /// The generation this key belongs to, in the spelling <c>CredentialPort</c> stores.
    /// </summary>
    /// <remarks>
    /// The primary key, so there is at most one row per generation and a save replaces rather than
    /// appends. Each generation's key is independently replaceable for the same reason.
    /// </remarks>
    public string Generation { get; set; } = "";

    /// <summary>The key as the operator supplied it.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Server UTC ticks at which this row was last written.</summary>
    /// <remarks>
    /// Ticks in an integer column rather than a timestamp type, because the same statements have to
    /// run on the provider production uses and on the one the tests use.
    /// </remarks>
    public long UpdatedAtUtcTicks { get; set; }
}

/// <summary>
/// The credential table's physical schema: the migration the host applies, exactly as it ships.
/// </summary>
/// <remarks>
/// The host — not this extension — executes this SQL, receipts it by name, and never re-runs a name
/// it has already receipted. A failure is a host log line and the load continues, so the extension
/// can be enabled with no table behind it.
/// </remarks>
public static class WhisparrCredentialSchema
{
    /// <summary>The table this extension owns. Nothing namespaces extension tables in the shared context.</summary>
    public const string TableName = "whisparrsync_credentials";

    /// <summary>The migration's name, which is FROZEN: it must never change, and neither must its SQL.</summary>
    /// <remarks>
    /// The host receipts a migration by NAME and skips any name it has already applied, whatever the
    /// content now says. So editing this migration's statements later would reach a fresh install and
    /// never reach an existing one, and the two populations would diverge with nothing to notice.
    /// A schema change is therefore a NEW constant with a NEW name, added beside this one — never an
    /// edit to it.
    /// </remarks>
    public const string Migration001Name = "001_create_whisparrsync_credentials";

    /// <summary>
    /// The statement that creates the credential table. Create-if-absent, because the table can
    /// outlive its receipt in both directions: an uninstall deletes the extension's directory and
    /// nothing deletes the receipt, while a restored database can carry the table with no receipt at
    /// all. Re-running has to be harmless.
    /// </summary>
    /// <remarks>
    /// Nothing here is provider-specific, because the same string has to run on the database
    /// production uses and on the one the tests use. There is no auto-numbering column: the
    /// generation is the whole key, so a row has nothing to number.
    /// </remarks>
    public const string Migration001UpSql =
        """
        CREATE TABLE IF NOT EXISTS whisparrsync_credentials (
            generation           TEXT   NOT NULL,
            api_key              TEXT   NOT NULL,
            updated_at_utc_ticks BIGINT NOT NULL,
            PRIMARY KEY (generation)
        );
        """;
}
