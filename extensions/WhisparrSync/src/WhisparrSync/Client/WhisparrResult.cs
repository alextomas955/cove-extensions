namespace WhisparrSync.Client;

/// <summary>The classify-not-throw outcome states for an outbound Whisparr call.</summary>
internal enum WhisparrResultState
{
    /// <summary>The call succeeded and <see cref="WhisparrResult{T}.Value"/> is populated.</summary>
    Ok,

    /// <summary>Whisparr rejected the API key (HTTP 401/403).</summary>
    BadKey,

    /// <summary>Whisparr could not be reached (connection refused, timeout, or a non-2xx JSON response).</summary>
    Unreachable,

    /// <summary>The response was not the Whisparr API (e.g. a reverse-proxy HTML landing page / 502).</summary>
    NotWhisparr,

    /// <summary>Reached a Servarr instance whose major version this build cannot manage (VER-04).</summary>
    VersionMismatch,
}

/// <summary>
/// The classify-not-throw result of a Whisparr call: exactly one <see cref="WhisparrResultState"/> plus
/// the payload/diagnostic for that state. Mirrors this codebase's result-type idiom (Renamer's engine
/// results) so a transport/HTTP fault is a typed value the handler branches on, never an exception. All
/// states are defined now so the type is stable across the phase; the full branch handling lands in plan
/// 01-02 — this phase exercises the Ok / BadKey / Unreachable / NotWhisparr happy-path contract.
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

    /// <summary>A short diagnostic for <see cref="WhisparrResultState.Unreachable"/> (never the API key/URL-with-key).</summary>
    public string? Reason { get; }

    /// <summary>The detected version for <see cref="WhisparrResultState.VersionMismatch"/>.</summary>
    public string? DetectedVersion { get; }

    public bool IsOk => State == WhisparrResultState.Ok;

    public static WhisparrResult<T> Ok(T value) => new(WhisparrResultState.Ok, value, null, null);
    public static WhisparrResult<T> BadKey() => new(WhisparrResultState.BadKey, default, null, null);
    public static WhisparrResult<T> Unreachable(string reason) => new(WhisparrResultState.Unreachable, default, reason, null);
    public static WhisparrResult<T> NotWhisparr() => new(WhisparrResultState.NotWhisparr, default, null, null);
    public static WhisparrResult<T> VersionMismatch(string detected) => new(WhisparrResultState.VersionMismatch, default, null, detected);
}
