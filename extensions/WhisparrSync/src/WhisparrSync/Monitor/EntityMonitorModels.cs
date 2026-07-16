namespace WhisparrSync.Monitor;

/// <summary>
/// The entity a monitor toggle targets — the single axis that keeps the studio and performer
/// flows symmetric: one parameterized-by-kind port pair (<see cref="Adapters.IWhisparrAdapter"/>)
/// rather than four methods. The adapter maps each kind to its version-specific wire shape (studio lookup by
/// <c>?stashId=</c> query, performer by path with 404/500-as-absent).
/// </summary>
internal enum EntityKind
{
    /// <summary>A StashDB studio — looked up in Whisparr by the <c>?stashId=</c> query param.</summary>
    Studio,

    /// <summary>A StashDB performer — looked up in Whisparr by path (HTTP 404/500 = not added).</summary>
    Performer,
}

/// <summary>
/// How far a monitor toggle cascades — the two scopes Whisparr itself exposes, so Cove reflects a choice
/// into Whisparr's own monitor state rather than imposing one.
/// </summary>
/// <remarks>
/// <see cref="NewReleases"/> monitors the container for future scenes only (v3 studio <c>monitored:true</c>;
/// v2 <c>monitorNewItems:"all"</c> with existing episodes left unmonitored). <see cref="AllScenes"/> also
/// marks the existing back-catalogue wanted (v3 bulk <c>PUT /movie/editor monitored:true</c> over the
/// attributed scenes; v2 <c>monitor:"all"</c> / episode-monitor). Loop-safety is invariant across both: the
/// add never grabs (<c>searchForMovie</c>/<c>searchForMissingEpisodes</c> stay false) — only an explicit
/// search grabs. AllScenes registers owned scenes as missing-in-Whisparr, so a later search can re-download
/// them (idempotent on re-import); NewReleases avoids that.
/// </remarks>
public enum MonitorScope
{
    /// <summary>Monitor the container for future scenes only; leave the existing back-catalogue unmonitored.</summary>
    NewReleases,

    /// <summary>Also mark every existing scene attributed to the entity as monitored (wanted).</summary>
    AllScenes,
}

/// <summary>
/// The outcome of a monitor toggle: the entity's resulting <see cref="Monitored"/>
/// state after the add-then-flip, and whether a create (<see cref="Added"/>) was performed by this call. A
/// re-toggle of an already-present entity reports <see cref="Added"/> <c>false</c> (the create's 409/exists is
/// success, never a duplicate) so the caller can distinguish a first add from an idempotent no-op.
/// </summary>
internal sealed record EntityMonitorResult(bool Added, bool Monitored);

/// <summary>
/// The quiet-status projection for a studio/performer: whether it is <see cref="Added"/> to Whisparr,
/// its current <see cref="Monitored"/> flag, and Whisparr's own "<see cref="ScenesPresent"/> of
/// <see cref="ScenesTotal"/>" count — scenes present in Whisparr's library over the entity's full StashDB
/// catalog, read verbatim off the Whisparr studio/performer resource (no StashDB call, no movie-set scan).
/// </summary>
/// <remarks>
/// <see cref="ScenesTotal"/> is 0 (so <see cref="HasCounts"/> is <c>false</c>) when Whisparr reports no
/// catalog for the entity; the UI then renders the bare "Monitored in Whisparr" line, never "0 of 0".
/// </remarks>
internal sealed record EntityStatus(bool Added, bool Monitored, int ScenesPresent, int ScenesTotal)
{
    /// <summary>
    /// True only when Whisparr reports a non-zero catalog for the entity, so the caller renders the
    /// "X of Y" count fragment; false degrades the status line to a bare "Monitored in Whisparr".
    /// </summary>
    public bool HasCounts => ScenesTotal > 0;
}
