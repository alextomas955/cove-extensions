namespace WhisparrSync.Client;

/// <summary>The classify-not-throw outcome states for an outbound Whisparr call.</summary>
internal enum WhisparrResultState
{
    /// <summary>The call succeeded and <see cref="WhisparrResult{T}.Value"/> is populated.</summary>
    Ok,

    /// <summary>Whisparr rejected the API key (HTTP 401/403).</summary>
    BadKey,

    /// <summary>Whisparr could not be reached (connection refused or timeout), or a non-2xx response
    /// carrying no actionable message. Distinct from <see cref="Rejected"/>, where Whisparr WAS reached.</summary>
    Unreachable,

    /// <summary>
    /// Whisparr was reached but declined the request: a non-2xx JSON body carrying its own error message
    /// (held in <see cref="WhisparrResult{T}.Reason"/>). Distinct from <see cref="Unreachable"/>, where no
    /// response arrived — so a caller surfaces Whisparr's reason instead of a reachability error.
    /// </summary>
    Rejected,

    /// <summary>The response was not the Whisparr API (e.g. a reverse-proxy HTML landing page / 502).</summary>
    NotWhisparr,

    /// <summary>Reached a Servarr instance whose major version this build cannot manage.</summary>
    VersionMismatch,

    /// <summary>
    /// The entity is not present in Whisparr: a performer/studio GET answered HTTP 404 or 500
    /// for a not-added entity. A data outcome the adapter reads as "not added yet" (then adds), never a
    /// transport fault — distinct from <see cref="Unreachable"/> so a misread state never proceeds.
    /// </summary>
    Absent,

    /// <summary>
    /// A create (POST) of an already-existing entity: Whisparr answered HTTP 409. A non-error
    /// outcome the caller resolves by re-reading the existing row, never a transport failure. (Some
    /// v3 builds instead answer a 2xx with the existing row, which classifies as <see cref="Ok"/>.)
    /// </summary>
    Conflict,

    /// <summary>
    /// An add Whisparr refused because the user's own import-exclusion configuration covers the scene's
    /// studio. A data outcome the caller reports as SKIPPED-by-user-config — never a transport fault and
    /// never a failure, because the refusal IS the user's configured intent.
    /// <see cref="WhisparrResult{T}.Reason"/> carries Whisparr's own message naming what was excluded.
    /// </summary>
    Excluded,

    /// <summary>
    /// The client refused to build the request because a value it would have interpolated into a filter or a
    /// path segment was blank. Whisparr was never reached and never asked, so this is neither a reachability
    /// fault (<see cref="Unreachable"/>) nor a refusal by Whisparr (<see cref="Rejected"/>) — and unlike
    /// <see cref="Absent"/> it is not a data outcome either: the caller holds no answer about Whisparr at all.
    /// </summary>
    NotAsked,
}

/// <summary>
/// The classify-not-throw result of a Whisparr call: exactly one <see cref="WhisparrResultState"/> plus
/// the payload/diagnostic for that state. Mirrors this codebase's result-type idiom (Renamer's engine
/// results) so a transport/HTTP fault is a typed value the handler branches on, never an exception. The
/// Ok / BadKey / Unreachable / NotWhisparr states cover the connection contract, and
/// <see cref="WhisparrResultState.Absent"/> (performer/studio 404/500),
/// <see cref="WhisparrResultState.Conflict"/> (create 409) and
/// <see cref="WhisparrResultState.Excluded"/> (an add the user's import exclusions cover) carry the
/// monitor/create data outcomes. <see cref="WhisparrResultState.NotAsked"/> sits outside both: no call
/// was made at all.
/// </summary>
internal sealed class WhisparrResult<T>
{
    private WhisparrResult(WhisparrResultState state, T? value, string? reason, string? detectedVersion)
    {
        State = state;
        Value = value;
        Reason = reason;
        DetectedVersion = detectedVersion;
    }

    public WhisparrResultState State { get; }

    /// <summary>The payload on <see cref="WhisparrResultState.Ok"/>; <c>default</c> otherwise.</summary>
    public T? Value { get; }

    /// <summary>
    /// A short diagnostic for <see cref="WhisparrResultState.Unreachable"/>, or Whisparr's own error message
    /// for <see cref="WhisparrResultState.Rejected"/> and <see cref="WhisparrResultState.Excluded"/> (never the
    /// API key/URL-with-key).
    /// </summary>
    public string? Reason { get; }

    /// <summary>The detected version for <see cref="WhisparrResultState.VersionMismatch"/>.</summary>
    public string? DetectedVersion { get; }

    public bool IsOk => State == WhisparrResultState.Ok;

    public static WhisparrResult<T> Ok(T value) => new(WhisparrResultState.Ok, value, null, null);
    public static WhisparrResult<T> BadKey() => new(WhisparrResultState.BadKey, default, null, null);
    public static WhisparrResult<T> Unreachable(string reason) => new(WhisparrResultState.Unreachable, default, reason, null);
    public static WhisparrResult<T> Rejected(string message) => new(WhisparrResultState.Rejected, default, message, null);
    public static WhisparrResult<T> NotWhisparr() => new(WhisparrResultState.NotWhisparr, default, null, null);
    public static WhisparrResult<T> VersionMismatch(string detected) => new(WhisparrResultState.VersionMismatch, default, null, detected);
    public static WhisparrResult<T> Absent() => new(WhisparrResultState.Absent, default, null, null);
    public static WhisparrResult<T> Conflict() => new(WhisparrResultState.Conflict, default, null, null);
    public static WhisparrResult<T> Excluded(string message) => new(WhisparrResultState.Excluded, default, message, null);
    public static WhisparrResult<T> NotAsked(string reason) => new(WhisparrResultState.NotAsked, default, reason, null);

    /// <summary>
    /// Reshapes a NON-Ok result of another payload type into this type, preserving the failure state (and its
    /// reason/detected-version). The single home for the state→failure mapping so a caller that must change the
    /// payload type on an early-out never re-implements — and drifts — the switch.
    /// </summary>
    public static WhisparrResult<T> PropagateFrom<TFrom>(WhisparrResult<TFrom> source)
        => FromFailure(source.State, source.Reason, source.DetectedVersion);

    /// <summary>
    /// The same state→failure mapping for a caller that holds only a preserved
    /// <see cref="WhisparrResultState"/>: a classification that crossed a seam projecting the state without the
    /// original result. <paramref name="reason"/> and <paramref name="detectedVersion"/> default to the generic
    /// text a caller with no diagnostic to carry would otherwise have to invent.
    /// </summary>
    public static WhisparrResult<T> FromFailure(
        WhisparrResultState state, string? reason = null, string? detectedVersion = null)
        => state switch
        {
            WhisparrResultState.BadKey => BadKey(),
            WhisparrResultState.NotWhisparr => NotWhisparr(),
            WhisparrResultState.VersionMismatch => VersionMismatch(detectedVersion ?? string.Empty),
            WhisparrResultState.Rejected => Rejected(reason ?? "rejected"),
            WhisparrResultState.Excluded => Excluded(reason ?? "excluded"),
            WhisparrResultState.NotAsked => NotAsked(reason ?? "not asked"),
            _ => Unreachable(reason ?? "unreachable"),
        };
}
