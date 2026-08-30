using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Cove.Extensions.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;

namespace WhisparrSync.Connection;

/// <summary>A secret one inbound request presented, and where it carried it.</summary>
/// <param name="Value">The secret as presented.</param>
/// <param name="Position">Where the request carried it.</param>
public sealed record PresentedCallbackSecret(string Value, CallbackSecretPosition Position);

/// <summary>
/// Mints this product's own callback secret and compares a presented one against it.
/// </summary>
/// <remarks>
/// The random source is the cryptographic one. A general-purpose generator is seeded from the clock
/// and its whole sequence is recoverable from any one output, which makes every deployment's secret
/// derivable from any deployment's.
/// </remarks>
public static class CallbackSecret
{
    /// <summary>How many random bytes a minted secret is drawn from.</summary>
    internal const int EntropyBytes = 32;

    /// <summary>A fresh secret, drawn from the cryptographic random source.</summary>
    /// <remarks>
    /// Base64url so the value survives a query string, a header value and a copy-paste unchanged, with
    /// nothing to escape in any of the three.
    /// </remarks>
    public static string Mint()
    {
        Span<byte> entropy = stackalloc byte[EntropyBytes];
        RandomNumberGenerator.Fill(entropy);
        return Base64Url.EncodeToString(entropy);
    }

    /// <summary>Whether <paramref name="presented"/> is <paramref name="stored"/>.</summary>
    /// <remarks>
    /// Compared over fixed-width digests rather than over the strings, so neither the comparison time
    /// nor an early length check tells a caller how much of a guess was right.
    /// </remarks>
    public static bool Matches(string? stored, string? presented)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        Span<byte> storedDigest = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> presentedDigest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(stored), storedDigest);
        SHA256.HashData(Encoding.UTF8.GetBytes(presented), presentedDigest);
        return CryptographicOperations.FixedTimeEquals(storedDigest, presentedDigest);
    }

    /// <summary>
    /// The secret an inbound request presented, or null when it presented none.
    /// </summary>
    /// <remarks>
    /// Either position is accepted. A one-click registration strips the secret from the address and
    /// carries it out of band; an address a user pasted by hand has nowhere else to put one, so the
    /// endpoint that receives both has to read both.
    /// <para>
    /// The out-of-band position wins when a request carries both, so a delivery from a registration
    /// this product made is never classified by a query string an intermediary could have appended.
    /// </para>
    /// </remarks>
    public static PresentedCallbackSecret? PresentedIn(string? outOfBand, string? inAddress)
    {
        if (!string.IsNullOrWhiteSpace(outOfBand))
        {
            return new PresentedCallbackSecret(outOfBand, CallbackSecretPosition.OutOfBand);
        }

        return string.IsNullOrWhiteSpace(inAddress)
            ? null
            : new PresentedCallbackSecret(inAddress, CallbackSecretPosition.Address);
    }
}

/// <summary>Reads and mints the one callback secret this extension holds.</summary>
/// <remarks>
/// The secret lives beside the API key, in a table this extension owns, rather than in the options
/// blob: Cove's bulk extension-data route returns an extension's stored values whole, and a secret
/// that authenticates an anonymous route is exactly what must not travel that way.
/// </remarks>
public interface ICallbackSecretPort
{
    /// <summary>The stored secret, or null when none has been minted.</summary>
    Task<string?> ReadAsync(CancellationToken ct);

    /// <summary>The stored secret, minting and storing one when none is held.</summary>
    /// <remarks>
    /// Returns the secret that is stored after the call, which is the one already there when a
    /// concurrent caller won the insert.
    /// </remarks>
    Task<string> EnsureAsync(DateTimeOffset nowUtc, CancellationToken ct);
}

/// <inheritdoc cref="ICallbackSecretPort"/>
internal sealed class CallbackSecretPort(DbContext db) : ICallbackSecretPort
{
    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        var row = await db.Set<WhisparrSecretEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(secret => secret.Name == WhisparrSecretSchema.CallbackSecretName, ct)
            .ConfigureAwait(false);

        return row?.Secret;
    }

    public async Task<string> EnsureAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (await ReadAsync(ct).ConfigureAwait(false) is { } held)
        {
            return held;
        }

        db.Set<WhisparrSecretEntity>().Add(new WhisparrSecretEntity
        {
            Name = WhisparrSecretSchema.CallbackSecretName,
            Secret = CallbackSecret.Mint(),
            UpdatedAtUtcTicks = nowUtc.UtcTicks,
        });

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The name is the primary key, so a concurrent mint loses this insert rather than
            // producing a second secret. Whichever row is there is the one every later request is
            // authenticated against, so it is the answer.
            db.ChangeTracker.Clear();
            return await ReadAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The callback secret insert was refused and no stored secret was found afterwards.");
        }

        return await ReadAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The callback secret was written and did not read back.");
    }

    /// <summary>Reads the stored secret over a scope of its own, elevated to System.</summary>
    /// <remarks>
    /// The entry point for the inbound callback route, which runs under no principal at all. Cove's
    /// per-principal query filters return zero rows with no error for an Anonymous caller, which would
    /// report a stored secret as absent and turn every delivery into a refusal.
    /// </remarks>
    /// <param name="scopes">The scope factory the request was resolved from.</param>
    /// <param name="ct">Cancels the operation.</param>
    public static Task<string?> ReadInSystemScopeAsync(IServiceScopeFactory scopes, CancellationToken ct)
        => RunAsSystem.RunInSystemScopeAsync(
            scopes,
            services => new CallbackSecretPort(services.GetRequiredService<DbContext>()).ReadAsync(ct));
}
