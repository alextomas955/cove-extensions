using WhisparrSync.Contracts;
using WhisparrSync.Options;

namespace WhisparrSync.Connection;

/// <summary>
/// Maps between the stored settings and the shapes the settings page reads and writes.
/// </summary>
/// <remarks>
/// Pure in both directions: no store, no host, no clock. The key never passes through here at all —
/// the outward projection is told only whether one exists, and the inward mapping hands the submitted
/// field straight to the port's own rule.
/// </remarks>
public static class SettingsProjector
{
    /// <summary>The settings page's view of <paramref name="options"/>.</summary>
    /// <param name="options">The stored settings.</param>
    /// <param name="v3KeyIsSet">Whether a key is stored for v3.</param>
    /// <param name="v2KeyIsSet">Whether a key is stored for v2.</param>
    public static WhisparrSyncSettingsView ToView(
        WhisparrSyncOptions options, bool v3KeyIsSet, bool v2KeyIsSet)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new WhisparrSyncSettingsView(
            options.SelectedGeneration,
            ViewOf(options.V3, v3KeyIsSet),
            ViewOf(options.V2, v2KeyIsSet));
    }

    /// <summary>
    /// <paramref name="stored"/> with <paramref name="request"/> applied.
    /// </summary>
    /// <remarks>
    /// A generation whose address moves loses its recorded version, the instant that version was
    /// verified, and the instant it last answered, because all three described a different instance.
    /// A save that leaves the address where it points keeps them.
    /// </remarks>
    /// <param name="stored">The settings as they are now.</param>
    /// <param name="request">The save to apply.</param>
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
        };
    }

    /// <summary>The key write <paramref name="save"/> asks for.</summary>
    /// <remarks>
    /// An omitted generation and an omitted signal both keep the stored key. A replacement is handed to
    /// <see cref="CredentialWrite.FromSubmitted"/> rather than branched on here, so the rule that a
    /// submitted blank keeps the stored key stays in one place.
    /// </remarks>
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
