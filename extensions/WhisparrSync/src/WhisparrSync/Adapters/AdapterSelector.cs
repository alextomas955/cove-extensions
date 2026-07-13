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
    // RED stub — the real fail-closed parse + gate land in the GREEN phase of this task.
    internal static int ParseMajor(string? version)
    {
        _ = version;
        return -1;
    }

    internal static IWhisparrAdapter? Select(SystemStatus status, WhisparrClient client)
    {
        _ = status;
        _ = client;
        return null;
    }
}
