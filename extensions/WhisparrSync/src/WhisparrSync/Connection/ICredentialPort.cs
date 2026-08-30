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
/// are different requests, and a caller that had only "a string or nothing" would have to encode the
/// difference as a convention nothing enforces.
/// </remarks>
public sealed record CredentialWrite
{
    private CredentialWrite(CredentialWriteKind kind, string? apiKey)
    {
        Kind = kind;
        ApiKey = apiKey;
    }

    /// <summary>Which of the three this write is.</summary>
    public CredentialWriteKind Kind { get; }

    /// <summary>The key to store, present only on <see cref="CredentialWriteKind.Replace"/>.</summary>
    public string? ApiKey { get; }

    /// <summary>Leaves the stored key untouched.</summary>
    public static CredentialWrite Keep { get; } = new(CredentialWriteKind.Keep, null);

    /// <summary>Removes the stored key.</summary>
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
    /// The rule that a blank field keeps the stored key lives here and nowhere else, so a second call
    /// site cannot disagree with it. An explicit removal reaches <see cref="Clear"/> by its own route
    /// rather than by submitting a blank.
    /// </remarks>
    public static CredentialWrite FromSubmitted(string? apiKey)
        => string.IsNullOrWhiteSpace(apiKey) ? Keep : Replace(apiKey);
}

/// <summary>Reads and writes the API key this extension holds for each Whisparr generation.</summary>
/// <remarks>
/// The key is here rather than in the options blob because Cove's bulk extension-data route returns
/// an extension's stored values whole, to any caller its permission filter admits.
/// </remarks>
public interface ICredentialPort
{
    /// <summary>The key stored for <paramref name="generation"/>, or null when none is.</summary>
    /// <remarks>
    /// Null means no key is stored. A caller that cannot distinguish that from a read it was not
    /// allowed to make would treat a permission problem as a missing key, which is why a background
    /// read runs as System.
    /// </remarks>
    Task<string?> ReadAsync(WhisparrGeneration generation, CancellationToken ct);

    /// <summary>Whether a key is stored for <paramref name="generation"/>.</summary>
    /// <remarks>
    /// Separate from <see cref="ReadAsync"/> so a caller that only has to say whether a key exists
    /// never holds one. The settings response is built from this.
    /// </remarks>
    Task<bool> HasKeyAsync(WhisparrGeneration generation, CancellationToken ct);

    /// <summary>Applies <paramref name="write"/> to <paramref name="generation"/>'s stored key.</summary>
    /// <param name="generation">The generation whose key this save addresses.</param>
    /// <param name="write">Which of the three writes the save is.</param>
    /// <param name="nowUtc">The instant recorded against the row when one is written.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task ApplyAsync(
        WhisparrGeneration generation, CredentialWrite write, DateTimeOffset nowUtc, CancellationToken ct);
}
