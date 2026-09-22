using WhisparrSync.Connection;
using WhisparrSync.Contracts;

namespace WhisparrSync.Tests.TestSupport;

// Records write arguments, not counts: a counter would pass a handler that wrote the wrong
// generation's key. The arguments say which generation was written and with which write kind.
internal sealed class RecordingCredentialPort : ICredentialPort
{
    private readonly Dictionary<WhisparrGeneration, string> _keys = [];
    private readonly Dictionary<WhisparrGeneration, string> _addresses = [];

    public List<(WhisparrGeneration Generation, CredentialWriteKind Kind, string? ApiKey)> Writes { get; } = [];

    public List<WhisparrGeneration> Reads { get; } = [];

    public RecordingCredentialPort Holding(WhisparrGeneration generation, string apiKey)
    {
        _keys[generation] = apiKey;
        return this;
    }

    /// <summary>Holds the pair an outbound request is built from.</summary>
    public RecordingCredentialPort Holding(
        WhisparrGeneration generation, string address, string apiKey)
    {
        _keys[generation] = apiKey;
        _addresses[generation] = address;
        return this;
    }

    public Task<string?> ReadAsync(WhisparrGeneration generation, CancellationToken ct)
    {
        Reads.Add(generation);
        return Task.FromResult(_keys.GetValueOrDefault(generation));
    }

    public Task<bool> HasKeyAsync(WhisparrGeneration generation, CancellationToken ct)
        => Task.FromResult(_keys.ContainsKey(generation));

    public Task<WhisparrStoredConnection?> ReadConnectionAsync(
        WhisparrGeneration generation, CancellationToken ct)
    {
        Reads.Add(generation);
        return Task.FromResult(
            _keys.TryGetValue(generation, out var apiKey)
                ? new WhisparrStoredConnection(_addresses.GetValueOrDefault(generation, ""), apiKey)
                : null);
    }

    public Task ApplyAsync(
        WhisparrGeneration generation,
        CredentialWrite write,
        string address,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);
        Writes.Add((generation, write.Kind, write.ApiKey));
        // Written whatever the key write does, as production writes it: the address and the key are
        // one row, so a double that kept them apart could not show a torn pair being impossible.
        _addresses[generation] = address;

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
