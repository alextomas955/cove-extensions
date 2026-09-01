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
    /// Describes the address that was in the field. It records no version reading, because the instance
    /// it reached may be one the user is only considering rather than the one that is stored.
    /// </remarks>
    Task<ConnectionTestView> TestTransientAsync(string? address, string? apiKey, CancellationToken ct);

    /// <summary>Tests the stored connection of the generation the settings currently select.</summary>
    /// <remarks>
    /// The only path that records a version reading, and only on a success: this is the one call that
    /// knows the instance it reached is the stored one.
    /// </remarks>
    Task<ConnectionTestView> TestStoredAsync(CancellationToken ct);
}

/// <inheritdoc cref="IConnectionTestRunner"/>
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

        // Reachability is recorded only when the address tested is the one that is stored, and the
        // comparison is against the address the gate loads: a settings save committed while the probe
        // was in flight may have moved it.
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

        // A generation nothing has configured stands in as a connection with a blank address, which the
        // read below refuses by naming the address — the same answer, without a second null arm.
        var connection = stored.ConnectionFor(stored.SelectedGeneration)
            ?? new WhisparrSyncGenerationConnection();
        var apiKey = await credentials
            .ReadAsync(stored.SelectedGeneration, ct)
            .ConfigureAwait(false);

        // The refusal is decided here rather than by handing an empty pair to the tester, so an
        // unconfigured connection reaches nothing that could make a request.
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

        // The reading is applied to the connection the gate loads, not to the one read before the
        // probe: another writer may have committed to the same record while the instance was being
        // asked, and only what this call learned is this call's to replace.
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

    /// <summary>Whether something at the address answered, whatever it answered with.</summary>
    /// <remarks>
    /// A rejected key counts: the instance was reached. Only the two kinds that describe an address
    /// nothing was asked of, or that asked and got nothing, do not.
    /// </remarks>
    private static bool InstanceAnswered(ConnectionFailureKind kind)
        => kind is not (ConnectionFailureKind.NotConfigured or ConnectionFailureKind.Unreachable);
}
