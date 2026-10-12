using WhisparrSync.Contracts;
using WhisparrSync.Options;

namespace WhisparrSync.Connection;

/// <summary>
/// Maps between the stored settings and the shapes the settings page reads and writes.
/// </summary>
/// <remarks>
/// No API key passes through here. The outward projection is told only whether one exists, and the
/// inward mapping hands the submitted field straight to the port's own rule.
/// </remarks>
public static class SettingsProjector
{
    /// <summary>The settings page's view of <paramref name="options"/>.</summary>
    /// <remarks>
    /// The addresses come from the credential rows rather than the blob, because that is where an
    /// outbound request reads them. The page then names the instance a request would reach.
    /// </remarks>
    public static WhisparrSyncSettingsView ToView(
        WhisparrSyncOptions options,
        WhisparrStoredConnection? v3Stored,
        WhisparrStoredConnection? v2Stored)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new WhisparrSyncSettingsView(
            options.SelectedGeneration,
            ViewOf(options.V3, v3Stored),
            ViewOf(options.V2, v2Stored),
            options.UpgradeBehavior);
    }

    /// <summary>
    /// <paramref name="stored"/> with <paramref name="request"/> applied.
    /// </summary>
    /// <remarks>
    /// A generation whose address moves loses its recorded version, the instant that version was
    /// verified, and the instant it last answered, because all three described a different instance.
    /// A save that leaves the address where it points keeps them. An omitted upgrade behaviour leaves
    /// the stored one, so the connection form can save without restating a setting it does not show.
    /// </remarks>
    public static WhisparrSyncOptions Apply(
        WhisparrSyncOptions stored,
        WhisparrSyncSettingsSaveRequest request,
        string? v3AddressBefore,
        string? v2AddressBefore)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(request);

        return stored with
        {
            SelectedGeneration = request.SelectedGeneration,
            V3 = ApplyToGeneration(stored.V3, request.V3, v3AddressBefore),
            V2 = ApplyToGeneration(stored.V2, request.V2, v2AddressBefore),
            UpgradeBehavior = request.UpgradeBehavior ?? stored.UpgradeBehavior,
        };
    }

    /// <summary>The key write <paramref name="save"/> asks for.</summary>
    /// <remarks>
    /// An omitted generation and an omitted signal both keep the stored key. A replacement is handed
    /// to <see cref="CredentialWrite.FromSubmitted"/>, so the rule that a submitted blank keeps the
    /// stored key stays in one place.
    /// </remarks>
    /// <summary>The address this save leaves stored for a generation, normalised as the row holds it.</summary>
    /// <remarks>
    /// A generation the save omits keeps the address already stored for it, which is why the stored
    /// one is passed in rather than read as an empty string.
    /// </remarks>
    public static string AddressFor(
        WhisparrSyncGenerationSaveRequest? save, string? storedAddress)
        => save is null
            ? storedAddress ?? ""
            : ConnectionTester.NormaliseAddress(save.Address);

    public static CredentialWrite CredentialWriteFor(WhisparrSyncGenerationSaveRequest? save)
        => save?.KeyWrite switch
        {
            KeyWriteSignal.Replace => CredentialWrite.FromSubmitted(save.ApiKey),
            KeyWriteSignal.Clear => CredentialWrite.Clear,
            _ => CredentialWrite.Keep,
        };

    // A row exists once either column is held, so a key with no address still reports a key.
    private static WhisparrSyncGenerationSettingsView ViewOf(
        WhisparrSyncGenerationConnection? connection, WhisparrStoredConnection? stored)
        => new(
            stored?.Address ?? "",
            !string.IsNullOrEmpty(stored?.ApiKey),
            connection?.RecordedVersion,
            connection?.VersionVerifiedAtUtc,
            connection?.LastReachableAtUtc);

    // What the record describes is whichever instance the credential row named when it was written,
    // so the address before this save is what decides whether it still describes one.
    private static WhisparrSyncGenerationConnection? ApplyToGeneration(
        WhisparrSyncGenerationConnection? stored,
        WhisparrSyncGenerationSaveRequest? save,
        string? addressBefore)
    {
        if (save is null)
        {
            return stored;
        }

        // A save that names a generation leaves a record for it, empty where nothing has been learnt
        // yet. The record is what a later reading is written onto, and its absence is a generation
        // this reader has never configured.
        var address = ConnectionTester.NormaliseAddress(save.Address);
        return ConnectionTester.IsSameAddress(addressBefore, address)
            ? stored ?? new WhisparrSyncGenerationConnection()
            : new WhisparrSyncGenerationConnection();
    }
}
