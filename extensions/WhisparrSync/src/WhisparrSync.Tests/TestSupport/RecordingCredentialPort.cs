using WhisparrSync.Connection;
using WhisparrSync.Contracts;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// An in-memory <see cref="ICredentialPort"/> that records the ARGUMENTS of every write it is handed.
/// </summary>
/// <remarks>
/// A call counter would report that a refused handler wrote nothing while a handler that wrote the
/// wrong generation's key passed unchanged. The recorded arguments are what let a test say which
/// generation was written and with which of the three writes.
/// </remarks>
internal sealed class RecordingCredentialPort : ICredentialPort
{
    private readonly Dictionary<WhisparrGeneration, string> _keys = [];

    /// <summary>Every write this port was handed, in order.</summary>
    public List<(WhisparrGeneration Generation, CredentialWriteKind Kind, string? ApiKey)> Writes { get; } = [];

    /// <summary>Every generation whose key was READ, in order.</summary>
    public List<WhisparrGeneration> Reads { get; } = [];

    /// <summary>Stores <paramref name="apiKey"/> against <paramref name="generation"/> as a starting state.</summary>
    public RecordingCredentialPort Holding(WhisparrGeneration generation, string apiKey)
    {
        _keys[generation] = apiKey;
        return this;
    }

    public Task<string?> ReadAsync(WhisparrGeneration generation, CancellationToken ct)
    {
        Reads.Add(generation);
        return Task.FromResult(_keys.GetValueOrDefault(generation));
    }

    public Task<bool> HasKeyAsync(WhisparrGeneration generation, CancellationToken ct)
        => Task.FromResult(_keys.ContainsKey(generation));

    public Task ApplyAsync(
        WhisparrGeneration generation, CredentialWrite write, DateTimeOffset nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);
        Writes.Add((generation, write.Kind, write.ApiKey));

        switch (write.Kind)
        {
            case CredentialWriteKind.Replace:
                _keys[generation] = write.ApiKey!;
                break;
            case CredentialWriteKind.Clear:
                _keys.Remove(generation);
                break;
            case CredentialWriteKind.Keep:
            default:
                break;
        }

        return Task.CompletedTask;
    }
}
