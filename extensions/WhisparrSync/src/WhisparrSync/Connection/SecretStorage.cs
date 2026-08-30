namespace WhisparrSync.Connection;

/// <summary>
/// One secret this extension minted for itself, mapped to the extension-owned table in
/// <c>WhisparrSync.Data.cs</c> and created by <see cref="WhisparrSecretSchema"/>'s migration.
/// </summary>
/// <remarks>
/// A storage type, not a domain model: nothing outside <c>CallbackSecretPort</c> and the model
/// configuration should name it.
/// <para>
/// Separate from the credential table because the two hold different things: a credential is one the
/// operator supplied, per generation, and this is one this product minted for itself, per install.
/// </para>
/// </remarks>
public sealed class WhisparrSecretEntity
{
    /// <summary>Which of this extension's own secrets the row holds.</summary>
    /// <remarks>
    /// The primary key, so a second mint under the same name is refused by the database rather than
    /// producing a second secret that half the deployment authenticates against.
    /// </remarks>
    public string Name { get; set; } = "";

    /// <summary>The secret value.</summary>
    public string Secret { get; set; } = "";

    /// <summary>Server UTC ticks at which this row was written.</summary>
    public long UpdatedAtUtcTicks { get; set; }
}

/// <summary>The secret table's physical schema: the migration the host applies, exactly as it ships.</summary>
public static class WhisparrSecretSchema
{
    /// <summary>The table this extension owns. Nothing namespaces extension tables in the shared context.</summary>
    public const string TableName = "whisparrsync_secrets";

    /// <summary>The name the callback secret is stored under.</summary>
    /// <remarks>Persisted data, so this string is as frozen as the migration that created the column.</remarks>
    public const string CallbackSecretName = "callback";

    /// <summary>The migration's name, which is FROZEN: it must never change, and neither must its SQL.</summary>
    /// <remarks>
    /// The host receipts a migration by NAME and skips any name it has already applied, whatever the
    /// content now says. A schema change is a NEW constant with a NEW name, added beside this one.
    /// </remarks>
    public const string Migration002Name = "002_create_whisparrsync_secrets";

    /// <summary>
    /// The statement that creates the secret table. Create-if-absent, because the table can outlive
    /// its receipt in both directions.
    /// </summary>
    /// <remarks>
    /// Nothing here is provider-specific, because the same string has to run on the database
    /// production uses and on the one the tests use.
    /// </remarks>
    public const string Migration002UpSql =
        """
        CREATE TABLE IF NOT EXISTS whisparrsync_secrets (
            name                 TEXT   NOT NULL,
            secret               TEXT   NOT NULL,
            updated_at_utc_ticks BIGINT NOT NULL,
            PRIMARY KEY (name)
        );
        """;
}
