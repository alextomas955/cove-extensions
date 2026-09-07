namespace WhisparrSync.Monitor;

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
