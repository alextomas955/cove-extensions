using WhisparrSync.Connection;
using WhisparrSync.Contracts;

namespace WhisparrSync.Tests.TestSupport;

// Records write arguments, not counts: a counter would pass a handler that wrote the wrong
// generation's key. The arguments say which generation was written and with which write kind.
internal sealed class RecordingCredentialPort : ICredentialPort
{
    private readonly Dictionary<WhisparrGeneration, string> _keys = [];

    public List<(WhisparrGeneration Generation, CredentialWriteKind Kind, string? ApiKey)> Writes { get; } = [];

    public List<WhisparrGeneration> Reads { get; } = [];

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
