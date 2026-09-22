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

    public async Task<WhisparrStoredConnection?> ReadConnectionAsync(
        WhisparrGeneration generation, CancellationToken ct)
    {
        var key = StoredNameOf(generation);
        var row = await db.Set<WhisparrCredentialEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(credential => credential.Generation == key, ct)
            .ConfigureAwait(false);
        return row is null ? null : new WhisparrStoredConnection(row.Address, row.ApiKey);
    }

    public async Task ApplyAsync(
        WhisparrGeneration generation,
        CredentialWrite write,
        string address,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);

        var key = StoredNameOf(generation);
        var rows = db.Set<WhisparrCredentialEntity>();
        var stored = await rows
            .FirstOrDefaultAsync(credential => credential.Generation == key, ct)
            .ConfigureAwait(false);

        if (write.Kind == CredentialWriteKind.Keep)
        {
            // The key is left alone and the address still written: the two travel together, and a
            // row naming the instance before this save is the pair a request would be built from.
            if (stored is not null && stored.Address != address)
            {
                stored.Address = address;
                stored.UpdatedAtUtcTicks = nowUtc.UtcTicks;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return;
        }

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
                Address = address,
                UpdatedAtUtcTicks = nowUtc.UtcTicks,
            });
        }
        else
        {
            stored.ApiKey = write.ApiKey!;
            // Written with the key, never separately: the two are one outbound value.
            stored.Address = address;
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
