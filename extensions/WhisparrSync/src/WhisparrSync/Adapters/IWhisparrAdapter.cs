namespace WhisparrSync.Adapters;

/// <summary>How an owned file is attached to its Whisparr scene — the orchestration selects this from the layout.</summary>
internal enum OwnedImportMode
{
    /// <summary>
    /// Re-point the existing movie row's path to Cove's own scene folder and rescan, so Whisparr links the file
    /// already sitting there — zero duplication, regardless of Whisparr's hardlink setting. The folder-per-scene
    /// path (v3).
    /// </summary>
    InPlaceAdopt,

    /// <summary>
    /// Copy the owned file into the movie's own folder via a targeted <c>ManualImport</c> — the fallback the
    /// orchestration selects for a flat (shared-directory) layout, where a path re-point would collide two movies
    /// on one directory.
    /// </summary>
    Copy,
}

/// <summary>
/// The aggregate marker for the surface EVERY managed Whisparr generation honors: the 7 shared role
/// interfaces (connect/config, reconcile-read, activity-read, webhook-admin, owned-import, scene-search,
/// studio-monitor). Both <see cref="V2Adapter"/> and <see cref="V3Adapter"/> implement it, so
/// <see cref="AdapterSelector"/> hands a caller one type carrying every version-invariant capability. A v3-only
/// capability (performer-monitor, scene-push, scene-lookup, status-index, exclusions, release-grab,
/// file-settings) is NOT on this aggregate — a caller narrows to its role interface
/// (<c>adapter is IWhisparrScenePush</c>), and a v2 instance structurally lacks it, so the former
/// <c>Supports*</c> probes are gone (presence replaces them).
/// </summary>
internal interface IWhisparrAdapter
    : IWhisparrConnection,
        IWhisparrReconcileSource,
        IWhisparrActivitySource,
        IWhisparrWebhookAdmin,
        IWhisparrOwnedImport,
        IWhisparrSceneSearch,
        IWhisparrStudioMonitor;
