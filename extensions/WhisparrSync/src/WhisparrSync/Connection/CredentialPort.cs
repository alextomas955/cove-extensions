using Cove.Extensions.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;

namespace WhisparrSync.Connection;

// Takes DbContext rather than the host's concrete context. The host registers its context resolvable
// as the base type, so this compiles with no reference to the host's data assembly. One instance
// wraps one scope's context.
internal sealed class CredentialPort(DbContext db) : ICredentialPort
{
    public async Task<string?> ReadAsync(WhisparrGeneration generation, CancellationToken ct)
    {
        var key = StoredNameOf(generation);
        var row = await db.Set<WhisparrCredentialEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(credential => credential.Generation == key, ct)
            .ConfigureAwait(false);

        return row?.ApiKey;
    }

    public Task<bool> HasKeyAsync(WhisparrGeneration generation, CancellationToken ct)
    {
        var key = StoredNameOf(generation);
        return db.Set<WhisparrCredentialEntity>()
            .AsNoTracking()
            .AnyAsync(credential => credential.Generation == key, ct);
    }

    public async Task ApplyAsync(
        WhisparrGeneration generation, CredentialWrite write, DateTimeOffset nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);

        if (write.Kind == CredentialWriteKind.Keep)
        {
            return;
        }

        var key = StoredNameOf(generation);
        var rows = db.Set<WhisparrCredentialEntity>();
        var stored = await rows
            .FirstOrDefaultAsync(credential => credential.Generation == key, ct)
            .ConfigureAwait(false);

        if (write.Kind == CredentialWriteKind.Clear)
        {
            if (stored is null)
            {
                return;
            }

            rows.Remove(stored);
        }
        else if (stored is null)
        {
            rows.Add(new WhisparrCredentialEntity
            {
                Generation = key,
                ApiKey = write.ApiKey!,
                UpdatedAtUtcTicks = nowUtc.UtcTicks,
            });
        }
        else
        {
            stored.ApiKey = write.ApiKey!;
            stored.UpdatedAtUtcTicks = nowUtc.UtcTicks;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // The one entry point for a background path, elevated to System. Under an under-privileged
    // principal, Cove's per-principal query filters return zero rows with no error, which reads as
    // "there is no key" rather than "I could not check".
    public static Task<string?> ReadInSystemScopeAsync(
        IServiceScopeFactory scopes, WhisparrGeneration generation, CancellationToken ct)
        => RunAsSystem.RunInSystemScopeAsync(
            scopes,
            services => new CredentialPort(services.GetRequiredService<DbContext>()).ReadAsync(generation, ct));

    // Persisted data, so these two strings are frozen. An unnamed generation throws rather than
    // falling back, because a fallback would write one generation's key under another's name.
    internal static string StoredNameOf(WhisparrGeneration generation)
        => generation switch
        {
            WhisparrGeneration.V3 => "v3",
            WhisparrGeneration.V2 => "v2",
            _ => throw new ArgumentOutOfRangeException(nameof(generation), generation, null),
        };
}
