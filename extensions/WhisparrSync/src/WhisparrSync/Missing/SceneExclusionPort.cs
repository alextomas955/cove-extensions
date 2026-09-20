using WhisparrSync.Whisparr;

namespace WhisparrSync.Missing;

// The instance narrows its exclusion list by no parameter, so the read is the whole list. It is
// consumed as it arrives and each row is reduced to whether it names one of the page's own
// identifiers, so nothing kept here grows with what the instance holds.
//
// The reading role is a parameter rather than held state: which generation is connected is a
// stored setting.
internal static class SceneExclusionPort
{
    // One request per page derivation, never one per card.
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
