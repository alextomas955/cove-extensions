using System.Text.Json.Nodes;
using WhisparrSync.Import;

namespace WhisparrSync.Whisparr;

// The inbound half of one generation's wire vocabulary: which member of a payload its instance sent
// names the imported file and which names the scene's catalogue identifier. A shared path holds
// none of those spellings and reads through the reader for the generation that sent the payload.
internal interface IWhisparrPayloadReading
{
    // The webhook body member carrying the imported file.
    string WebhookFileMember { get; }

    // Null where the body names no identifying value.
    string? WebhookRemoteId(JsonObject body);
}

internal static class PayloadMember
{
    // A number renders as its invariant text, so an identifier carried as a JSON number on one
    // generation and a JSON string on the other reaches the core in one form. Both readers answer
    // through the guard, so neither can accept an identifier the other would refuse.
    internal static string? Identifier(JsonObject? owner, string member)
    {
        if (owner?[member] is not JsonValue value)
        {
            return null;
        }

        return RemoteIdGuard.Identifying(
            value.TryGetValue<string>(out var text) ? text : value.ToString());
    }
}
