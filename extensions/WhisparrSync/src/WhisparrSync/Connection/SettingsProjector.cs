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
    public static WhisparrSyncSettingsView ToView(
        WhisparrSyncOptions options, bool v3KeyIsSet, bool v2KeyIsSet)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new WhisparrSyncSettingsView(
            options.SelectedGeneration,
            ViewOf(options.V3, v3KeyIsSet),
            ViewOf(options.V2, v2KeyIsSet),
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
        WhisparrSyncOptions stored, WhisparrSyncSettingsSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(request);

        return stored with
        {
            SelectedGeneration = request.SelectedGeneration,
            V3 = ApplyToGeneration(stored.V3, request.V3),
            V2 = ApplyToGeneration(stored.V2, request.V2),
            UpgradeBehavior = request.UpgradeBehavior ?? stored.UpgradeBehavior,
        };
    }

    /// <summary>The key write <paramref name="save"/> asks for.</summary>
    /// <remarks>
    /// An omitted generation and an omitted signal both keep the stored key. A replacement is handed
    /// to <see cref="CredentialWrite.FromSubmitted"/>, so the rule that a submitted blank keeps the
    /// stored key stays in one place.
    /// </remarks>
    /// <summary>The address this save leaves stored for a generation, normalised as the blob holds it.</summary>
    /// <remarks>
    /// Read from the same request the blob is projected from, so the row written beside the key and
    /// the blob the page reads cannot name different instances.
    /// </remarks>
    public static string AddressFor(
        WhisparrSyncGenerationSaveRequest? save, WhisparrSyncGenerationConnection? stored)
        => save is null
            ? stored?.Address ?? ""
            : ConnectionTester.NormaliseAddress(save.Address);

    public static CredentialWrite CredentialWriteFor(WhisparrSyncGenerationSaveRequest? save)
        => save?.KeyWrite switch
        {
            KeyWriteSignal.Replace => CredentialWrite.FromSubmitted(save.ApiKey),
            KeyWriteSignal.Clear => CredentialWrite.Clear,
            _ => CredentialWrite.Keep,
        };

    private static WhisparrSyncGenerationSettingsView ViewOf(
        WhisparrSyncGenerationConnection? connection, bool keyIsSet)
        => new(
            connection?.Address ?? "",
            keyIsSet,
            connection?.RecordedVersion,
            connection?.VersionVerifiedAtUtc,
            connection?.LastReachableAtUtc);

    private static WhisparrSyncGenerationConnection? ApplyToGeneration(
        WhisparrSyncGenerationConnection? stored, WhisparrSyncGenerationSaveRequest? save)
    {
        if (save is null)
        {
            return stored;
        }

        var address = ConnectionTester.NormaliseAddress(save.Address);
        return stored is not null && ConnectionTester.IsSameAddress(stored.Address, address)
            ? stored
            : new WhisparrSyncGenerationConnection { Address = address };
    }
}
