using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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

    /// <summary>The custom header this product's own callbacks are recognised by.</summary>
    /// <remarks>
    /// Declared here, on the inbound side that reads it, so the registration that sets it and the
    /// route that accepts it cannot name two different headers.
    /// </remarks>
    public const string CustomHeaderName = "X-Cove-Whisparr-Sync-Secret";

    /// <summary>The user name a Basic-auth registration carries beside the secret.</summary>
    /// <remarks>
    /// Fixed rather than configurable: it identifies the registration and carries no authority. The
    /// secret is the password half, which is the half a Whisparr connection stores under a
    /// <c>password</c> privacy.
    /// </remarks>
    public const string BasicAuthUser = "cove-whisparr-sync";

    /// <summary>The scheme a Basic-auth presentation uses.</summary>
    private const string BasicScheme = "Basic ";

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
    /// Every position a registration this product makes can use is accepted, and so is the address:
    /// a one-click registration strips the secret from the address and carries it out of band, while
    /// an address a user pasted by hand has nowhere else to put one.
    /// <para>
    /// An out-of-band position wins over the address when a request carries both, so a delivery from
    /// a registration this product made is never classified by a query string an intermediary could
    /// have appended.
    /// </para>
    /// </remarks>
    /// <param name="customHeader">The value of <see cref="CustomHeaderName"/>, if any.</param>
    /// <param name="authorization">The <c>Authorization</c> header, if any.</param>
    /// <param name="inAddress">The secret query parameter, if any.</param>
    public static PresentedCallbackSecret? PresentedIn(
        string? customHeader, string? authorization, string? inAddress)
    {
        if (!string.IsNullOrWhiteSpace(customHeader))
        {
            return new PresentedCallbackSecret(customHeader, CallbackSecretPosition.OutOfBand);
        }

        if (BasicAuthPasswordIn(authorization) is { } password)
        {
            return new PresentedCallbackSecret(password, CallbackSecretPosition.OutOfBand);
        }

        return string.IsNullOrWhiteSpace(inAddress)
            ? null
            : new PresentedCallbackSecret(inAddress, CallbackSecretPosition.Address);
    }

    /// <summary>
    /// The password half of a Basic-auth header, or null when there is none to read.
    /// </summary>
    /// <remarks>
    /// The password rather than the whole credential, because the user name identifies the
    /// registration and the secret is what is checked. Split on the FIRST colon: a password may
    /// contain one and a user name may not.
    /// </remarks>
    private static string? BasicAuthPasswordIn(string? authorization)
    {
        if (authorization is null
            || !authorization.StartsWith(BasicScheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(authorization[BasicScheme.Length..].Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        var decoded = Encoding.UTF8.GetString(raw);
        var separator = decoded.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0 || separator == decoded.Length - 1)
        {
            return null;
        }

        return decoded[(separator + 1)..];
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
internal sealed class CallbackSecretPort(DbContext db, ILogger log) : ICallbackSecretPort
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
            WhisparrSyncLog.ConcurrentMintLostToAnExistingRow(log);
            db.ChangeTracker.Clear();
            return await ReadAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The callback secret insert was refused and no stored secret was found afterwards.");
        }

        return await ReadAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The callback secret was written and did not read back.");
    }
}
