using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

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
    /// call that knows the instance it reached is the stored one. It is also the only path that
    /// re-reads the callback registration, for the same reason.
    /// </remarks>
    Task<ConnectionTestView> TestStoredAsync(CancellationToken ct);
}

internal sealed class ConnectionTestRunner(
    IWhisparrConnectionTester tester,
    OptionsStore options,
    OptionsWriteGate gate,
    ICredentialPort credentials,
    IWhisparrNotificationPort notifications,
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

        // Compared against the address stored when the gate is held, not the one read earlier: a
        // settings save committed while the probe was in flight may have moved it, and this result
        // then describes an instance the row no longer names.
        var stillThere = false;
        await gate.MutateAfterAsync(
            options,
            async (loaded, readCt) =>
            {
                var held = await credentials
                    .ReadConnectionAsync(loaded.SelectedGeneration, readCt).ConfigureAwait(false);
                stillThere = ConnectionTester.IsSameAddress(held?.Address, address);
            },
            stored => stillThere && stored.ConnectionFor(stored.SelectedGeneration) is { } connection
                ? stored.WithConnectionFor(
                    stored.SelectedGeneration, connection with { LastReachableAtUtc = reachableAt })
                : stored,
            ct).ConfigureAwait(false);
        return view;
    }

    public async Task<ConnectionTestView> TestStoredAsync(CancellationToken ct)
    {
        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        var generation = stored.SelectedGeneration;

        // Resolved the way every outbound request is, so this reports on the instance a request
        // would reach. A probe of the options address would answer for a connection nothing sends
        // to once a save has moved the row the key sits in. The refusal for an unconfigured
        // connection comes from the same resolution, so nothing here could make a request without
        // both settings.
        var resolution = await OutboundPair
            .ResolveAsync(credentials, generation, ct).ConfigureAwait(false);
        if (resolution.Binding is not { } binding)
        {
            return ConnectionTestView.NotConfigured(
                resolution.Missing!.Value, resolution.Address?.ToString());
        }

        var view = await tester
            .TestAsync(binding.BaseAddress.ToString(), binding.ApiKey, ct)
            .ConfigureAwait(false);
        if (!InstanceAnswered(view.Kind))
        {
            return view;
        }

        var now = clock.GetUtcNow();
        var connected = view.Kind == ConnectionFailureKind.Connected;

        // Read back here and nowhere else. Opening the settings page deliberately asks the instance
        // nothing, because a failed ask there is indistinguishable from an absent registration; this
        // gesture has already reached the instance, so the same ask is attributable. Without it a
        // registration the instance no longer holds, after a reset or a hand-deleted connection,
        // goes on being reported as present and no import arrives.
        var registration = connected
            ? await ReadRegistrationAsync(binding, ct).ConfigureAwait(false)
            : RegistrationStatus.NotCheckedYet;

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
                    CallbackRegistration = Settled(current.CallbackRegistration),
                }
                : current with { LastReachableAtUtc = now };

        RegistrationStatus Settled(RegistrationStatus last)
            => registration == RegistrationStatus.NotCheckedYet ? last : registration;
    }

    // A reading that did not arrive settles nothing, so the status held from the last check stands.
    // The probe this follows is what the view reports on, and a registration read is not allowed to
    // turn a connection the instance answered into a failed test.
    private async Task<RegistrationStatus> ReadRegistrationAsync(
        WhisparrBinding binding, CancellationToken ct)
    {
        try
        {
            return (await notifications.ReadAsync(binding, ct).ConfigureAwait(false)).Status;
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            return RegistrationStatus.NotCheckedYet;
        }
    }

    // A rejected key counts as an answer: the instance was reached.
    private static bool InstanceAnswered(ConnectionFailureKind kind)
        => kind is not (ConnectionFailureKind.NotConfigured or ConnectionFailureKind.Unreachable);
}
