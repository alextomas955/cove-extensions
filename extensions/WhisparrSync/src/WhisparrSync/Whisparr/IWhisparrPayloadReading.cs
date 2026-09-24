using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Import;

namespace WhisparrSync.Whisparr;

// The inbound half of one generation's wire vocabulary: which member of a payload its instance sent
// names the imported file, which names the entity a file was matched to, and which names the
// scene's catalogue identifier. A shared path holds none of those spellings and reads through the
// reader for the generation that sent the payload.
internal interface IWhisparrPayloadReading
{
    // The webhook body member carrying the imported file.
    string WebhookFileMember { get; }

    // Null where the body names no identifying value.
    string? WebhookRemoteId(JsonObject body);

    // Read from the same catalogue the live channel reads for this generation, so an arrival
    // through either channel is the same scene. Null where the record embeds no entity, which is
    // imported without an identifier rather than refused.
    string? HistoryRemoteId(JsonObject record);

    // The importable row's member naming the entity the file was matched to.
    string MatchedMember { get; }

    // The submit entry for one matched row, composed onto the members every generation carries, or
    // null where the row names nothing this generation can attach a file to.
    JsonObject? MatchedEntry(JsonObject row, JsonObject entry);

    /// <summary>
    /// The entry addressed to the scene the library identified, or null where this generation
    /// cannot address one by a single id.
    /// </summary>
    JsonObject? IdentifiedEntry(JsonObject entry, int entityId);
}

// Which monitor scope an instance's answer puts in force. Held by the generation whose resource
// carries the date gate and by no other: a generation expressing no scope holds no reading of one,
// which is a different fact from a reading that always answers null.
internal interface IWhisparrScopeReading
{
    MonitorScope? ScopeIn(WhisparrEntityKind kind, JsonObject entity);
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

    // The instance-side row id of the entity a file was matched to. An absent member, one of
    // another type and a non-positive id all read as no match.
    // The file's own name, as the instance spells its path. A caller pairs it against what the
    // library holds for the same folder.
    internal static string? NameIn(JsonObject row)
    {
        if (row["path"] is not JsonValue named || !named.TryGetValue<string>(out var path))
        {
            return null;
        }

        var cut = path.Replace('\\', '/').LastIndexOf('/');
        return cut < 0 ? path : path[(cut + 1)..];
    }

    internal static int? MatchedId(JsonObject row, string member)
        => row[member] is JsonObject matched
            && matched["id"] is JsonValue named
            && named.TryGetValue<int>(out var id)
            && id > 0
                ? id
                : null;
}
