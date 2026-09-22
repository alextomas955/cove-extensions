using System.Text.Json.Nodes;

namespace WhisparrSync.Whisparr;

// What this generation's instance sends, read where it sends it. No scope reading: the date gate
// belongs to a resource this generation does not serve.
internal sealed class V2PayloadReader : IWhisparrPayloadReading
{
    internal static V2PayloadReader Reading { get; } = new();

    public string WebhookFileMember => "episodeFile";

    // A number on the scene rows the delivery lists, where v3 carries a string beside the entity.
    // The first scene's is taken, because a delivery reports one imported file.
    public string? WebhookRemoteId(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return PayloadMember.Identifier(
            (body["episodes"] as JsonArray)?.FirstOrDefault() as JsonObject, "tvdbId");
    }

    public string? HistoryRemoteId(JsonObject record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return PayloadMember.Identifier(record["episode"] as JsonObject, "tvdbId");
    }

    public string MatchedMember => "series";

    // Transcribed from the interface bundle this generation's build ships: a series, and the
    // episodes matched inside it.
    public JsonObject? MatchedEntry(JsonObject row, JsonObject entry)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(entry);

        if (PayloadMember.MatchedId(row, MatchedMember) is not { } seriesId
            || row["episodes"] is not JsonArray episodes)
        {
            return null;
        }

        var episodeIds = new JsonArray();
        foreach (var episode in episodes.OfType<JsonObject>())
        {
            if (episode["id"] is JsonValue named && named.TryGetValue<int>(out var episodeId))
            {
                episodeIds.Add(episodeId);
            }
        }

        if (episodeIds.Count == 0)
        {
            return null;
        }

        entry["seriesId"] = seriesId;
        entry["episodeIds"] = episodeIds;
        entry["episodeFileId"] = row["episodeFileId"]?.DeepClone();
        return entry;
    }
}
