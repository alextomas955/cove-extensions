using WhisparrSync.Contracts;

namespace WhisparrSync.Connection;

/// <summary>What a save says about the API key.</summary>
public enum CredentialWriteKind
{
    /// <summary>Leave the stored key as it is.</summary>
    Keep,

    /// <summary>Store the supplied key in place of whatever is there.</summary>
    Replace,

    /// <summary>Remove the stored key.</summary>
    Clear,
}

/// <summary>One save's instruction for the API key of one generation.</summary>
/// <remarks>
/// Three signals, not two. A form that submits no key and a form that asks for the key to be removed
/// are different requests.
/// </remarks>
public sealed record CredentialWrite
{
    private CredentialWrite(CredentialWriteKind kind, string? apiKey)
    {
        Kind = kind;
        ApiKey = apiKey;
    }

    public CredentialWriteKind Kind { get; }

    /// <summary>The key to store, present only on <see cref="CredentialWriteKind.Replace"/>.</summary>
    public string? ApiKey { get; }

    public static CredentialWrite Keep { get; } = new(CredentialWriteKind.Keep, null);

    public static CredentialWrite Clear { get; } = new(CredentialWriteKind.Clear, null);

    /// <summary>Stores <paramref name="apiKey"/>, replacing any key already held.</summary>
    /// <exception cref="ArgumentException"><paramref name="apiKey"/> is null, empty or whitespace.</exception>
    public static CredentialWrite Replace(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return new CredentialWrite(CredentialWriteKind.Replace, apiKey);
    }

    /// <summary>Reads a submitted key field as one of the three writes.</summary>
    /// <remarks>
    /// The rule that a blank field keeps the stored key lives here and nowhere else. An explicit
    /// removal reaches <see cref="Clear"/> by its own route, not by submitting a blank.
    /// </remarks>
    public static CredentialWrite FromSubmitted(string? apiKey)
        => string.IsNullOrWhiteSpace(apiKey) ? Keep : Replace(apiKey);
}

/// <summary>Reads and writes the API key this extension holds for each Whisparr generation.</summary>
/// <remarks>
/// The key is here rather than in the options blob because Cove's bulk extension-data route returns
/// an extension's stored values whole, to any caller its permission filter admits.
/// </remarks>
/// <summary>One generation's outbound target, as one value.</summary>
public sealed record WhisparrStoredConnection(string Address, string ApiKey);

public interface ICredentialPort
{
    /// <summary>The key stored for <paramref name="generation"/>, or null when none is.</summary>
    /// <remarks>
    /// A read that is not allowed also returns null, so a background read runs as System rather than
    /// reporting a permission problem as a missing key.
    /// </remarks>
    Task<string?> ReadAsync(WhisparrGeneration generation, CancellationToken ct);

    /// <summary>Whether a key is stored for <paramref name="generation"/>.</summary>
    /// <remarks>
    /// Separate from <see cref="ReadAsync"/> so a caller that only reports whether a key exists never
    /// holds one. The settings response is built from this.
    /// </remarks>
    Task<bool> HasKeyAsync(WhisparrGeneration generation, CancellationToken ct);

    /// <summary>The instance and key stored together for <paramref name="generation"/>.</summary>
    /// <remarks>
    /// The pair an outbound request is built from, read in one go, and the only place an outbound
    /// address comes from. Taking the address from the options blob and the key from here would let
    /// a reader observe one from either side of a save that changed both, and post the new key to
    /// the instance the old address names.
    /// <para>
    /// A row carrying no address is not a connection. Nothing is sent for it, and the refusal names
    /// the address as the setting that is missing.
    /// </para>
    /// </remarks>
    Task<WhisparrStoredConnection?> ReadConnectionAsync(
        WhisparrGeneration generation, CancellationToken ct);

    /// <summary>Applies <paramref name="write"/> and <paramref name="address"/> as one row.</summary>
    /// <remarks>
    /// The address is written whatever the write does to the key, including
    /// <see cref="CredentialWriteKind.Keep"/>: a save that moves the instance and leaves the key
    /// alone would otherwise leave this row naming the instance before it, which is the pair an
    /// outbound request is built from.
    /// </remarks>
    Task ApplyAsync(
        WhisparrGeneration generation,
        CredentialWrite write,
        string address,
        DateTimeOffset nowUtc,
        CancellationToken ct);
}
