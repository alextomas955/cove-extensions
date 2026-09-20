using System.Text.Json;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Scene;

// Every answer read here was composed by a third party, so an unparseable body, an empty one and
// a body naming no numeric identifier each return null instead of throwing.
//
// Confirmation is identifier equality and nothing else. The status a just-posted command reports
// is not read: which value that is on a given build is unmeasured.
internal static class CommandProjector
{
    internal static int? IdIn(WhisparrResponse posted)
    {
        ArgumentNullException.ThrowIfNull(posted);
        return IdentifierIn(posted);
    }

    // Confirms only that the instance holds the command. It says nothing about progress and
    // nothing about anything having been downloaded.
    internal static bool Confirmed(WhisparrResponse? held, int commandId)
        => held is not null && IdentifierIn(held) == commandId;

    private static int? IdentifierIn(WhisparrResponse answer)
    {
        if (answer.StatusCode is not (>= 200 and < 300))
        {
            return null;
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(answer.Body);
        }
        catch (JsonException)
        {
            return null;
        }

        using (parsed)
        {
            return parsed.RootElement.ValueKind == JsonValueKind.Object
                && parsed.RootElement.TryGetProperty("id", out var named)
                && named.ValueKind == JsonValueKind.Number
                && named.TryGetInt32(out var id)
                && id >= 1
                    ? id
                    : null;
        }
    }
}
