using WhisparrSync.Client;
using WhisparrSync.Push;

namespace WhisparrSync.Adapters;

/// <summary>
/// Add / monitor an independent scene — v3-ONLY. v2 has no <c>POST /episode</c> (a scene exists only under a
/// site), so a v2 adapter never implements this role and every per-scene add path structurally defers on v2
/// BEFORE resolving the origin tag / root folder, keeping a deferred v2 add wire-free.
/// </summary>
internal interface IWhisparrScenePush
{
    /// <summary>
    /// Adds a scene to Whisparr as a movie by its StashDB id. CRITICAL loop-safety:
    /// <paramref name="searchForMovie"/> DEFAULTS to <c>false</c> so an add registers without grabbing — the flag
    /// exists only so the invariant is testable; no caller passes <c>true</c>. A 2xx returns the created movie
    /// (<c>Added:true</c>); an HTTP 409 (or a re-read that finds it) resolves to the existing row (<c>Added:false</c>)
    /// — idempotent, never a duplicate.
    /// </summary>
    Task<WhisparrResult<SceneActionResult>> AddSceneAsync(
        string baseUrl,
        string apiKey,
        string stashId,
        string? title,
        bool monitored,
        bool searchForMovie,
        string rootFolderPath,
        int qualityProfileId,
        IReadOnlyList<int> tagIds,
        CancellationToken ct);

    /// <summary>
    /// Sets a scene's monitor state via add-then-flip: absent + ON adds the movie <c>monitored:false</c> (with
    /// <c>searchForMovie:false</c> — never grabs on add) then PUTs <c>monitored:true</c>; present PUTs the requested
    /// state; absent + OFF is a no-op success. NEVER triggers a search.
    /// </summary>
    Task<WhisparrResult<SceneActionResult>> SetSceneMonitorAsync(
        string baseUrl,
        string apiKey,
        string stashId,
        string? title,
        bool monitored,
        string rootFolderPath,
        int qualityProfileId,
        IReadOnlyList<int> tagIds,
        CancellationToken ct);
}
