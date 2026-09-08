using System.Text.Json;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Scene;

/// <summary>What an instance's own answers say about a command that was posted to it.</summary>
/// <remarks>
/// Total in both members. Every answer read here was composed by a third party, so an unparseable
/// body, an empty one and a body naming no numeric identifier each answer the absent value rather
/// than throwing.
/// <para>
/// Confirmation is identifier equality and nothing else. The status a just-posted command reports is
/// not read: which value that is on a given build is unmeasured, and a decision resting on it would
/// be resting on a vocabulary nobody established.
/// </para>
/// </remarks>
internal static class CommandProjector
{
    /// <summary>The command the answer to a post names, or none it named.</summary>
    internal static int? IdIn(WhisparrResponse posted)
    {
        ArgumentNullException.ThrowIfNull(posted);
        return IdentifierIn(posted);
    }

    /// <summary>
    /// Whether <paramref name="held"/> is the instance's own answer for command
    /// <paramref name="commandId"/>.
    /// </summary>
    /// <remarks>
    /// A null answer is a read that produced none, which establishes nothing and confirms nothing.
    /// Whatever progress a confirmed answer reports is left unread: this says the instance holds the
    /// command, and deliberately says nothing about anything having been downloaded.
    /// </remarks>
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
