using System.Text.Json.Nodes;

namespace WhisparrSync.Whisparr;

// What this generation's instance sends, read where it sends it.
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
}
