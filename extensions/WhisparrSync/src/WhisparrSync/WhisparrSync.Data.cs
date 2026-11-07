using Microsoft.EntityFrameworkCore;
using WhisparrSync.Connection;

namespace WhisparrSync;

// The base class already declares the data-extension capability interface, so overriding these two
// members is the whole contract. The host calls DefineMigrations repeatedly after clearing the list,
// so it must stay deterministic and free of side effects.
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
            credential.Property(c => c.Address).HasColumnName("address");
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
