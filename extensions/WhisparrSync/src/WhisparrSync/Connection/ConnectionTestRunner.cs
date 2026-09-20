using WhisparrSync.Contracts;
using WhisparrSync.Options;

namespace WhisparrSync.Connection;

/// <summary>
/// The two ways a connection test is asked for, and what each one is allowed to record.
/// </summary>
public interface IConnectionTestRunner
{
    /// <summary>Tests an address and key that were supplied with the request.</summary>
    /// <remarks>
    /// Records no version reading: the instance it reached may be one the user is only considering
    /// rather than the stored one.
    /// </remarks>
    Task<ConnectionTestView> TestTransientAsync(string? address, string? apiKey, CancellationToken ct);

    /// <summary>Tests the stored connection of the generation the settings currently select.</summary>
    /// <remarks>
    /// The only path that records a version reading, and only on a success, because it is the one
    /// call that knows the instance it reached is the stored one.
    /// </remarks>
    Task<ConnectionTestView> TestStoredAsync(CancellationToken ct);
}

internal sealed class ConnectionTestRunner(
    IWhisparrConnectionTester tester,
    OptionsStore options,
    OptionsWriteGate gate,
    ICredentialPort credentials,
    TimeProvider clock) : IConnectionTestRunner
{
    public async Task<ConnectionTestView> TestTransientAsync(
        string? address, string? apiKey, CancellationToken ct)
    {
        var view = await tester.TestAsync(address, apiKey, ct).ConfigureAwait(false);
        if (!InstanceAnswered(view.Kind))
        {
            return view;
        }

        var reachableAt = clock.GetUtcNow();

        // Compared against the address the gate loads, not the one read earlier: a settings save
        // committed while the probe was in flight may have moved it.
        await gate.MutateAsync(
            options,
            stored => stored.ConnectionFor(stored.SelectedGeneration) is { } connection
                && ConnectionTester.IsSameAddress(connection.Address, address)
                    ? stored.WithConnectionFor(
                        stored.SelectedGeneration, connection with { LastReachableAtUtc = reachableAt })
                    : stored,
            ct).ConfigureAwait(false);
        return view;
    }

    public async Task<ConnectionTestView> TestStoredAsync(CancellationToken ct)
    {
        var stored = await options.LoadAsync(ct).ConfigureAwait(false);

        // A generation nothing has configured stands in as a connection with a blank address, which
        // the read below refuses by naming the address.
        var connection = stored.ConnectionFor(stored.SelectedGeneration)
            ?? new WhisparrSyncGenerationConnection();
        var apiKey = await credentials
            .ReadAsync(stored.SelectedGeneration, ct)
            .ConfigureAwait(false);

        // Refused here, so an unconfigured connection reaches nothing that could make a request.
        if (!ConnectionTester.TryReadConnection(connection.Address, apiKey, out var baseAddress, out var missing))
        {
            return ConnectionTestView.NotConfigured(missing, baseAddress?.ToString());
        }

        var view = await tester.TestAsync(connection.Address, apiKey, ct).ConfigureAwait(false);
        if (!InstanceAnswered(view.Kind))
        {
            return view;
        }

        var now = clock.GetUtcNow();
        var generation = stored.SelectedGeneration;
        var connected = view.Kind == ConnectionFailureKind.Connected;

        // Applied to the connection the gate loads, not the one read before the probe: another writer
        // may have committed to the same record while the instance was being asked.
        await gate.MutateAsync(
            options,
            fresh => fresh.WithConnectionFor(
                generation,
                Recording(fresh.ConnectionFor(generation) ?? new WhisparrSyncGenerationConnection())),
            ct).ConfigureAwait(false);
        return view;

        WhisparrSyncGenerationConnection Recording(WhisparrSyncGenerationConnection current)
            => connected
                ? current with
                {
                    LastReachableAtUtc = now,
                    RecordedVersion = view.Version,
                    VersionVerifiedAtUtc = now,
                }
                : current with { LastReachableAtUtc = now };
    }

    // A rejected key counts as an answer: the instance was reached.
    private static bool InstanceAnswered(ConnectionFailureKind kind)
        => kind is not (ConnectionFailureKind.NotConfigured or ConnectionFailureKind.Unreachable);
}
