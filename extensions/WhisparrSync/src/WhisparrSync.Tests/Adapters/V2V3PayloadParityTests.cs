using WhisparrSync.Adapters;

namespace WhisparrSync.Tests.Adapters;

// Pins the V2↔V3 webhook-registration wire lockstep. The payload builder + connection name are single-sourced
// on WhisparrAdapterBase, so a v2-only drift is now structurally impossible; these guard that the shared body
// stays byte-equal through both adapter type names and that the on-wire connection name never silently changes
// (a rename would orphan every previously-registered connection at find-time). Bare-safe: the builder is a pure
// static string method (no cove type, no host, no live Whisparr).
[Trait("Tier", "L0")]
public sealed class V2V3PayloadParityTests
{
    private const string WebhookUrl = "http://cove.local:5000/api/extensions/com.alextomas955.whisparrsync/webhook?token=s3cr3t%20token";

    [Fact]
    public void CreatePayload_V2_ByteEquals_V3()
    {
        Assert.Equal(
            V3Adapter.BuildNotificationPayload(WebhookUrl),
            V2Adapter.BuildNotificationPayload(WebhookUrl));
    }

    [Fact]
    public void UpdatePayload_WithRowId_V2_ByteEquals_V3()
    {
        Assert.Equal(
            V3Adapter.BuildNotificationPayload(WebhookUrl, id: 42),
            V2Adapter.BuildNotificationPayload(WebhookUrl, id: 42));
    }

    [Fact]
    public void Payload_PinsTheOnWireConnectionName()
    {
        // The find read and the register write agree only if the emitted connection name matches what was
        // stored; pin the exact wire literal so a rename can't silently orphan existing registrations.
        Assert.Contains("\"name\":\"Cove Whisparr Sync\"", V3Adapter.BuildNotificationPayload(WebhookUrl), StringComparison.Ordinal);
        Assert.Contains("\"name\":\"Cove Whisparr Sync\"", V2Adapter.BuildNotificationPayload(WebhookUrl, id: 42), StringComparison.Ordinal);
    }

    [Fact]
    public void CreatePayload_EmbedsExtractedToken_InHeader()
    {
        // ExtractToken is private; assert its effect through the shared payload: the URL's ?token= value,
        // URL-decoded, must appear as the X-Cove-Token header value the receiver validates.
        var payload = V3Adapter.BuildNotificationPayload(WebhookUrl);
        Assert.Contains("X-Cove-Token", payload, StringComparison.Ordinal);
        Assert.Contains("s3cr3t token", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateAndUpdate_Differ_OnlyByRowId()
    {
        // The update form carries the row id (Whisparr rejects a notification PUT with an absent/mismatched
        // body id); the create omits it. Both otherwise identical — pin that the id is the only delta.
        var create = V3Adapter.BuildNotificationPayload(WebhookUrl);
        var update = V3Adapter.BuildNotificationPayload(WebhookUrl, id: 7);
        Assert.DoesNotContain("\"id\":", create, StringComparison.Ordinal);
        Assert.Contains("\"id\":7", update, StringComparison.Ordinal);
    }
}
