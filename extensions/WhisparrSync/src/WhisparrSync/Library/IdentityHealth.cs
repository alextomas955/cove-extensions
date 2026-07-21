namespace WhisparrSync.Library;

/// <summary>
/// The pure library-wide identity-health count: over a set of <see cref="CoveVideo"/> DTOs, how many carry
/// NO provider id on the connected version's endpoint (StashDB on v3, ThePornDB on v2). Takes only the
/// already-loaded DTOs — no DB, no client — so it is unit-testable host-free and reused by the
/// <c>/identity-health</c> read after a single library load.
/// </summary>
internal static class IdentityHealth
{
    /// <summary>
    /// Counts <paramref name="videos"/> and the subset with no provider id on the connected endpoint.
    /// </summary>
    /// <param name="videos">Every Cove video (each already filtered to the configured endpoints).</param>
    /// <param name="useTpdbEndpoint">True on v2 (count against <see cref="CoveVideo.TpdbIds"/>), else v3 (<see cref="CoveVideo.StashIds"/>).</param>
    /// <returns>The whole-library total and the unidentified subset — <c>unidentified ≤ total</c>.</returns>
    public static (int Total, int Unidentified) Count(IEnumerable<CoveVideo> videos, bool useTpdbEndpoint)
    {
        int total = 0, unidentified = 0;
        foreach (var video in videos)
        {
            total++;
            var ids = useTpdbEndpoint ? video.TpdbIds : video.StashIds;
            if (ids.Count == 0 || ids.All(string.IsNullOrEmpty))
            {
                unidentified++;
            }
        }

        return (total, unidentified);
    }
}
