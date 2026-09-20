using WhisparrSync.Whisparr;

namespace WhisparrSync.Missing;

/// <summary>Which of one page's scenes the instance's user has excluded.</summary>
/// <remarks>
/// An excluded scene has left the missing set, so it does not render at all and there is no state a
/// card could carry for it.
/// <para>
/// The instance narrows its exclusion list by no parameter, so the read is the whole list. It is
/// consumed as it arrives and each row is reduced to whether it names one of the page's own
/// identifiers, so nothing derived from the response outlives the call and nothing here grows with
/// what the instance holds.
/// </para>
/// <para>
/// The reading role is a parameter, for the reason the status port's is: which generation is
/// connected is a stored setting. This holds nothing either, so it is static too.
/// </para>
/// </remarks>
internal static class SceneExclusionPort
{
    /// <summary>
    /// Which of <paramref name="providerSceneIds"/> the instance's user has excluded.
    /// </summary>
    /// <remarks>
    /// One request per page derivation, and never one per card. What the answer carries is bounded
    /// by the page whatever the instance holds.
    /// </remarks>
    public static async Task<IReadOnlySet<string>> ReadExcludedAsync(
        IWhisparrSceneExclusionReading reading,
        Uri baseAddress,
        string apiKey,
        IReadOnlyList<string> providerSceneIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(providerSceneIds);

        if (providerSceneIds.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return await reading
            .ReduceExclusionsAsync(baseAddress, apiKey, providerSceneIds, ct)
            .ConfigureAwait(false);
    }
}
