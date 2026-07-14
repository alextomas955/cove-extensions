using WhisparrSync.Client;

namespace WhisparrSync.Adapters;

/// <summary>
/// The fail-closed major-version gate (VER-03/VER-04). Both Whisparr v2 and v3 answer
/// <c>/api/v3/system/status</c> with a parseable body, so a "200 OK ⇒ connected" check would silently
/// misclassify an instance — the gate MUST branch on the parsed major version, never the status code.
/// major==3 selects the <see cref="V3Adapter"/>, major==2 selects the <see cref="V2Adapter"/>; a version
/// this build cannot manage (anything else) or an unparseable version is refused (returns <c>null</c>),
/// never a silent wrong-adapter call.
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
    /// Selects the adapter for the detected instance: a <see cref="V3Adapter"/> when the parsed major
    /// version is exactly 3, a <see cref="V2Adapter"/> when it is exactly 2, otherwise <c>null</c> (refuse).
    /// A <c>null</c> return is the VER-04 refusal — the caller surfaces a typed version-mismatch, never a
    /// wrong-adapter call.
    /// </summary>
    internal static IWhisparrAdapter? Select(SystemStatus status, WhisparrClient client)
        => ParseMajor(status.Version) switch
        {
            3 => new V3Adapter(client),
            2 => new V2Adapter(client),
            _ => null,
        };

    /// <summary>
    /// Selects the adapter for an already-persisted selection (the settings endpoints run after a
    /// successful test, so the version is known): a <see cref="V3Adapter"/> for <c>"v3"</c>, a
    /// <see cref="V2Adapter"/> for <c>"v2"</c>, otherwise <c>null</c> (refuse — VER-04). Case-insensitive.
    /// </summary>
    internal static IWhisparrAdapter? SelectForVersion(string? selectedVersion, WhisparrClient client)
    {
        if (string.Equals(selectedVersion, "v3", StringComparison.OrdinalIgnoreCase))
        {
            return new V3Adapter(client);
        }

        if (string.Equals(selectedVersion, "v2", StringComparison.OrdinalIgnoreCase))
        {
            return new V2Adapter(client);
        }

        return null;
    }
}
