using Microsoft.EntityFrameworkCore;
using WhisparrSync.Connection;

namespace WhisparrSync;

/// <summary>
/// The extension's data-extension hooks: the entity model it contributes to the host's context, and
/// the schema migration the host applies for it.
///
/// Kept apart from the rest of the extension for the same reason the logging partial is: this is a
/// host capability surface, defined once and never called from extension code.
/// </summary>
/// <remarks>
/// Nothing is added to the class declaration for this. The base class already declares the capability
/// interface, and the member that hands the host its migrations is not virtual — it clears the list
/// and calls <c>DefineMigrations</c> every time it is asked, so overriding these two is the whole
/// contract, and <c>DefineMigrations</c> must stay deterministic and free of side effects.
/// </remarks>
public sealed partial class WhisparrSync
{
    public override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WhisparrCredentialEntity>(credential =>
        {
            credential.ToTable(WhisparrCredentialSchema.TableName);
            credential.HasKey(c => c.Generation);
            credential.Property(c => c.Generation).HasColumnName("generation");
            credential.Property(c => c.ApiKey).HasColumnName("api_key");
            credential.Property(c => c.UpdatedAtUtcTicks).HasColumnName("updated_at_utc_ticks");
        });

        modelBuilder.Entity<WhisparrSecretEntity>(secret =>
        {
            secret.ToTable(WhisparrSecretSchema.TableName);
            secret.HasKey(s => s.Name);
            secret.Property(s => s.Name).HasColumnName("name");
            secret.Property(s => s.Secret).HasColumnName("secret");
            secret.Property(s => s.UpdatedAtUtcTicks).HasColumnName("updated_at_utc_ticks");
        });
    }

    protected override void DefineMigrations()
    {
        Migration(WhisparrCredentialSchema.Migration001Name, WhisparrCredentialSchema.Migration001UpSql);
        Migration(WhisparrSecretSchema.Migration002Name, WhisparrSecretSchema.Migration002UpSql);
    }
}
