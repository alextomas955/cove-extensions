using WhisparrSync.Client;

namespace WhisparrSync.Adapters;

/// <summary>
/// Attaches a file Cove ALREADY OWNS to its Whisparr scene in place — implemented by BOTH versions (v3 adopts
/// or copies the Eros movie's file, v2 registers a Sonarr episode via ManualImport). Cove's own file is NEVER
/// moved or deleted, and the path is never searched/grabbed.
/// </summary>
internal interface IWhisparrOwnedImport
{
    /// <summary>
    /// Imports a file Cove ALREADY OWNS into Whisparr, attaching it to <paramref name="scene"/> so Whisparr shows
    /// the scene as "have" — Cove's own file is NEVER moved or deleted, and it NEVER searches/grabs. The file must
    /// already sit where Whisparr can see it (shared storage), so <paramref name="whisparrFilePath"/> is the path
    /// AS WHISPARR SEES IT. <paramref name="mode"/> selects the v3 mechanism (v2 ignores it — it always registers
    /// its episode in place). A queued-but-unlinked outcome is <see cref="WhisparrResultState.Unreachable"/>,
    /// never a false Ok.
    /// </summary>
    Task<WhisparrResult<bool>> ImportOwnedSceneAsync(
        string baseUrl, string apiKey, WhisparrMovie scene, string whisparrFilePath, OwnedImportMode mode, CancellationToken ct);
}
