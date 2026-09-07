namespace WhisparrSync.Contracts;

// The neutral home for the cross-cutting boundary vocabulary the Cove-facing wire shares. MonitorScope and
// EntityKind are pure vocabulary (no type-level converter — their casing is applied at the property/usage
// site), so they live here, defined once. The third member, SceneWhisparrState, carries a TYPE-level
// [JsonConverter] binding it to its dedicated camelCase converter, so it stays defined with that behavior in
// SceneStatus/ (models live with their behavior); it is referenced, not redeclared, here.

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

    /// <summary>
    /// A metadata-source tag. Whisparr has no tag entity of its own, which is why a tag is never looked up
    /// there; its catalogue is always read direct from the metadata source — StashDB on the newer generation,
    /// ThePornDB on the older one, which serves the axis by filtering scenes on a tag.
    /// </summary>
    Tag,
}

/// <summary>
/// Which id family a read keys on — the connected Whisparr version's identity: StashDB on v3, ThePornDB on
/// v2. A v2 scene is a TPDB-keyed episode, so it must be subtracted by its TPDB id, never a StashDB id it
/// cannot carry.
/// </summary>
/// <remarks>Neutral rather than a member of the discovery slice: both that slice and the library read name it.</remarks>
internal enum DiscoveryIdFamily
{
    StashDb,
    Tpdb,
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
