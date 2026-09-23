namespace WhisparrSync.Connection;

/// <summary>
/// The stored API key for one Whisparr generation, mapped to the extension-owned table in
/// <c>WhisparrSync.Data.cs</c> and created by <see cref="WhisparrCredentialSchema"/>'s migration.
/// </summary>
/// <remarks>
/// A storage type, not a domain model: nothing outside <c>CredentialPort</c> and the model
/// configuration should name it.
/// </remarks>
public sealed class WhisparrCredentialEntity
{
    /// <summary>
    /// The generation this key belongs to, in the spelling <c>CredentialPort</c> stores. The primary
    /// key, so there is at most one row per generation and a save replaces rather than appends.
    /// </summary>
    public string Generation { get; set; } = "";

    /// <summary>The key as the operator supplied it.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// The instance this key belongs to, as the operator supplied it, or empty where none is stored.
    /// </summary>
    /// <remarks>
    /// Held beside the key rather than read from the options blob when a request is built. The two
    /// are one secret-bearing value: a reader that took the address from one store and the key from
    /// another can observe a pair from either side of a save that changed both, and post the new key
    /// to the instance the old address names.
    /// </remarks>
    public string Address { get; set; } = "";

    /// <summary>
    /// Server UTC ticks at which this row was last written. Ticks in an integer column, because the
    /// same statements run on the provider production uses and on the one the tests use.
    /// </summary>
    public long UpdatedAtUtcTicks { get; set; }
}

/// <summary>
/// The credential table's physical schema: the migration the host applies, exactly as it ships.
/// </summary>
/// <remarks>
/// The host, not this extension, executes this SQL and receipts it by name. A failure is a host log
/// line and the load continues, so the extension can be enabled with no table behind it.
/// </remarks>
public static class WhisparrCredentialSchema
{
    /// <summary>The table this extension owns. Nothing namespaces extension tables in the shared context.</summary>
    public const string TableName = "whisparrsync_credentials";

    /// <summary>The migration's name, which is frozen, as is its SQL.</summary>
    /// <remarks>
    /// The host receipts a migration by name and skips any name it has already applied, whatever the
    /// content now says. Editing these statements would reach a fresh install and never an existing
    /// one. A schema change is a new constant with a new name, added beside this one.
    /// </remarks>
    public const string Migration001Name = "001_create_whisparrsync_credentials";

    /// <summary>
    /// The statement that creates the credential table. Create-if-absent, because the table can
    /// outlive its receipt in both directions: an uninstall leaves the receipt, and a restored
    /// database can carry the table with no receipt at all.
    /// </summary>
    /// <remarks>
    /// Nothing here is provider-specific, because the same string runs on the database production
    /// uses and on the one the tests use.
    /// </remarks>
    public const string Migration001UpSql =
        """
        CREATE TABLE IF NOT EXISTS whisparrsync_credentials (
            generation           TEXT   NOT NULL,
            api_key              TEXT   NOT NULL,
            address              TEXT   NOT NULL DEFAULT '',
            updated_at_utc_ticks BIGINT NOT NULL,
            PRIMARY KEY (generation)
        );
        """;
}
