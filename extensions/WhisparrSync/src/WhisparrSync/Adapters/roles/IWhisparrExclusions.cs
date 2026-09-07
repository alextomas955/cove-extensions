using WhisparrSync.Client;

namespace WhisparrSync.Adapters;

/// <summary>
/// Import-list exclusions — v3-ONLY. v2 exclusions are TPDB-keyed and cannot be tied to a Cove scene id, so a
/// v2 adapter never implements this role. No exclusion path grabs.
/// </summary>
internal interface IWhisparrExclusions
{
    /// <summary>Lists the Whisparr import-list exclusion set — the source of the scene "Excluded" state.</summary>
    Task<WhisparrResult<WhisparrExclusion[]>> ListExclusionsAsync(string baseUrl, string apiKey, CancellationToken ct);

    /// <summary>
    /// Adds an exclusion for a scene by its StashDB id. Idempotent — a duplicate (409 or an "exists" 400 body)
    /// resolves to <c>Ok(true)</c>, never a duplicate row. Issues no search/command.
    /// </summary>
    Task<WhisparrResult<bool>> AddExclusionAsync(
        string baseUrl, string apiKey, string stashId, string? title, int? year, CancellationToken ct);

    /// <summary>
    /// Removes a scene's exclusion by its StashDB id. The exclusion's Whisparr id is resolved SERVER-SIDE by
    /// matching <paramref name="stashId"/> against the fetched list's <c>foreignId</c> — never a caller id. A
    /// scene with no matching exclusion is an idempotent <c>Ok(true)</c> no-op. Issues no search/command.
    /// </summary>
    Task<WhisparrResult<bool>> RemoveExclusionAsync(string baseUrl, string apiKey, string stashId, CancellationToken ct);
}
