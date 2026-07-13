using WhisparrSync.Client;

namespace WhisparrSync.Adapters;

/// <summary>
/// The fail-closed major-version gate (VER-04). Both Whisparr v2 and v3 answer <c>/api/v3/system/status</c>
/// with a parseable body, so a "200 OK ⇒ connected" check would silently misclassify a v2 instance — the
/// gate MUST branch on the parsed major version, never the status code. A version this build cannot manage
/// (anything but 3) or an unparseable version is refused (returns <c>null</c>), never a silent
/// wrong-adapter call.
/// </summary>
internal static class AdapterSelector
{
    /// <summary>The major-version sentinel returned for a null/empty/unparseable version (fail-closed).</summary>
    private const int Unparseable = -1;

    /// <summary>
    /// Parses the leading dotted segment of a Whisparr version string (e.g. <c>"3.3.4.808"</c> → <c>3</c>).
    /// Returns <see cref="Unparseable"/> (-1) for null/blank input or a non-numeric leading segment, so an
    /// unrecognizable version fails closed at the gate rather than defaulting to any adapter (Assumption A2).
    /// </summary>
    internal static int ParseMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return Unparseable;
        }

        var leadingSegment = version.Split('.', 2)[0];
        return int.TryParse(leadingSegment, out var major) && major >= 0 ? major : Unparseable;
    }

    /// <summary>
    /// Selects the adapter for the detected instance: a <see cref="V3Adapter"/> only when the parsed major
    /// version is exactly 3, otherwise <c>null</c> (refuse). A <c>null</c> return is the VER-04 refusal —
    /// the caller surfaces a typed version-mismatch, never a wrong-adapter call.
    /// </summary>
    internal static IWhisparrAdapter? Select(SystemStatus status, WhisparrClient client)
        => ParseMajor(status.Version) == 3 ? new V3Adapter(client) : null;
}
