using System.Text.Json.Nodes;

namespace WhisparrSync.Whisparr;

// What this generation's instance sends, read where it sends it.
internal sealed class V3PayloadReader : IWhisparrPayloadReading
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
}
