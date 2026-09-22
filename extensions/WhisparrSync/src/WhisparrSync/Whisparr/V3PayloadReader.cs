using System.Text.Json.Nodes;
using WhisparrSync.Contracts;

namespace WhisparrSync.Whisparr;

// What this generation's instance sends, read where it sends it.
internal sealed class V3PayloadReader : IWhisparrPayloadReading, IWhisparrScopeReading
{
    internal static V3PayloadReader Reading { get; } = new();

    public string WebhookFileMember => "movieFile";

    // A string beside the entity. v2 carries its own in a different place and as a different JSON
    // type.
    public string? WebhookRemoteId(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return PayloadMember.Identifier(body["movie"] as JsonObject, "stashId");
    }

    public string? HistoryRemoteId(JsonObject record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return PayloadMember.Identifier(record["movie"] as JsonObject, "stashId");
    }

    public string MatchedMember => "movie";

    // Transcribed from the interface bundle this generation's build ships: one scene per matched
    // row.
    public JsonObject? MatchedEntry(JsonObject row, JsonObject entry)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(entry);

        if (PayloadMember.MatchedId(row, MatchedMember) is not { } movieId)
        {
            return null;
        }

        entry["movieId"] = movieId;
        entry["movieFileId"] = row["movieFileId"]?.DeepClone();
        return entry;
    }

    // The date gate exists on this generation's studio resource and on no other, so a performer
    // answers null.
    //
    // Its presence is the whole reading. The value is never read and never compared against a
    // clock: what a scope covers on either side of that date is the instance's to decide. An
    // absent gate is the wider scope, which MonitorBodyPinTests transcribes.
    public MonitorScope? ScopeIn(WhisparrEntityKind kind, JsonObject entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (kind != WhisparrEntityKind.Studio)
        {
            return null;
        }

        return entity["afterDate"] is null ? MonitorScope.AllScenes : MonitorScope.FutureScenes;
    }
}
