using WhisparrSync.Options;

namespace WhisparrSync.Contracts;

/// <summary>One <c>/sync-preview</c> bucket: how many entities of a kind carry a connected-version id (WILL
/// sync) vs carry none (skipped). A bucket the connected version does not support is all-zero.</summary>
internal sealed record SyncPreviewCount(int WithId, int Skipped);

/// <summary>
/// The <c>/sync-preview</c> response: the sync-able-vs-skipped split per bucket, computed from a
/// whole-library System-principal read. <see cref="Performers"/> and <see cref="Scenes"/> are all-zero on
/// v2 (no performer entity, no per-scene add); <see cref="Studios"/> counts on both versions.
/// </summary>
internal sealed record SyncPreviewResponse(
    SyncPreviewCount Studios, SyncPreviewCount Performers, SyncPreviewCount Scenes);

/// <summary>
/// The <c>/sync-library</c> request body: whether to also monitor (<see cref="AlsoMonitor"/>, decoupled
/// from the add legs — the add runs regardless) and, when monitoring, the user-selected monitor
/// <see cref="Scope"/> (<c>NewReleases</c>/<c>AllScenes</c>, case-insensitive). A null/absent
/// <see cref="Scope"/> falls back to the stored <see cref="WhisparrOptions.DefaultMonitorScope"/>; the scope
/// choice does not affect loop-safety (an add never grabs on either scope).
/// </summary>
internal sealed record SyncLibraryRequest(bool AlsoMonitor, string? Scope = null);

/// <summary>The <c>/import-log</c> <c>syncHealth</c> view: unresolved path-mismatch import failures (Cove
/// couldn't open the path Whisparr reported) since the last success — the settings banner's data source.</summary>
internal sealed record SyncHealthView(int PathMismatch, long? LastMismatchTicks, IReadOnlyList<string> SamplePaths);

/// <summary>One <c>/import-log</c> <c>pipelineHealth</c> entry: a dependency's last classified outcome plus its
/// retained failure detail — the settings page's per-dependency readout.</summary>
/// <remarks>
/// The two ticks are nullable because the stored record uses zero for never, and a client rendering the epoch
/// would state a time that never happened. <see cref="LastError"/> is provider-controlled text: it is truncated
/// at write time and rendered as a text node, never as markup. It reads EMPTY for a recovered dependency whose
/// failure is older than <c>DependencyHealth.RecoveredErrorRetentionTicks</c>, while both ticks keep their values
/// indefinitely — so an empty error beside a non-null <see cref="LastFailureTicks"/> means the failure is past,
/// not that nothing went wrong. A dependency still failing keeps its error however old the failure is.
/// </remarks>
internal sealed record DependencyHealthView(
    string Dependency,
    string Outcome,
    long? LastHealthyTicks,
    long? LastFailureTicks,
    int ConsecutiveFailures,
    string LastError);

/// <summary>
/// Why the folder-overlap advisory compared nothing. Exactly one of these accompanies a <c>checked:false</c>
/// answer, so a read that could not run is distinguishable from a genuine all-clear.
/// </summary>
/// <remarks>
/// Each value names the REAL cause. The connected generation is deliberately not among them: the root read is
/// answered identically by both, so "not checked on this generation" would itself be a false statement. All four
/// ride a 200 — a configuration or availability state is not an HTTP error, and a 400 here would be swallowed by
/// the client's read catch into the same silent all-clear these values exist to remove.
/// </remarks>
internal static class FolderOverlapReason
{
    /// <summary>No Whisparr host is stored, so there was nothing to read roots from (no wire call is made).</summary>
    public const string NotConfigured = "notConfigured";

    /// <summary>Whisparr was configured but the root or naming read did not answer.</summary>
    public const string ReadFailed = "readFailed";

    /// <summary>Cove's own library roots could not be resolved, so no containment comparison is possible.</summary>
    public const string CoveRootsUnknown = "coveRootsUnknown";

    /// <summary>The persisted Whisparr version is one this build cannot manage (no wire call is made).</summary>
    public const string UnsupportedVersion = "unsupportedVersion";
}

/// <summary>The kinds of finding the folder-overlap advisory can report, and can declare unanswerable.</summary>
internal static class FolderFindingKind
{
    /// <summary>A Whisparr root and a Cove root where one contains (or equals) the other.</summary>
    public const string RootContainment = "rootContainment";

    /// <summary>A Whisparr root whose trailing segment doubles the Scene Folder Format's leading literal.</summary>
    public const string SceneFolderFormat = "sceneFolderFormat";
}

/// <summary>A root-containment finding: the two overlapping roots, AS SUPPLIED (un-normalized) so the user
/// recognizes them.</summary>
internal sealed record RootContainmentFinding(string Kind, string WhisparrRoot, string CoveRoot);

/// <summary>A scene-folder-format finding: the offending root, the doubled leading literal, and the root one
/// level above.</summary>
internal sealed record SceneFolderFindingView(string Kind, string Root, string Prefix, string SuggestedRoot);

/// <summary>
/// The folder-overlap answer. <see cref="Checked"/> is the discriminator: true means
/// <see cref="Findings"/> carries information (an empty array is then a genuine all-clear), false means nothing
/// was compared and <see cref="Reason"/> names why.
/// </summary>
/// <remarks>
/// <see cref="NotApplicable"/> lists the finding kinds this connection cannot answer AT ALL — distinct from a
/// reason, which is about the whole read. It is not an error state and not a prompt to change generation.
/// The handler returns facts only; every sentence is composed client-side.
/// </remarks>
internal sealed record FolderOverlapResponse(
    bool Checked, string? Reason, IReadOnlyList<object> Findings, IReadOnlyList<string> NotApplicable);
