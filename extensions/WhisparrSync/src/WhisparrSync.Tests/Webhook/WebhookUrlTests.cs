using System.Net;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Webhook;

namespace WhisparrSync.Tests.Webhook;

/// <summary>
/// Host-free contract for the webhook URL generation + best-effort auto-register: the secret is a
/// distinct high-entropy token reused for a stable URL, the URL embeds the token at the webhook path, the
/// v3 register payload is well-formed, and a non-2xx register is a non-Ok (registered:false) fallback rather
/// than a throw.
/// </summary>
public sealed class WebhookUrlTests
{
    private const string WebhookUrl = "http://cove.local/api/extensions/com.alextomas955.whisparrsync/webhook?token=abc";

    [Fact]
    public void MintSecret_produces_distinct_high_entropy_tokens()
    {
        var a = WebhookUrlBuilder.MintSecret();
        var b = WebhookUrlBuilder.MintSecret();

        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 32, "a 256-bit token base64url-encodes to well over 32 chars");
    }

    [Fact]
    public void EnsureSecret_reuses_a_stored_secret_and_mints_when_absent()
    {
        Assert.Equal("existing-secret", WebhookUrlBuilder.EnsureSecret("existing-secret"));

        var minted = WebhookUrlBuilder.EnsureSecret("");
        Assert.False(string.IsNullOrEmpty(minted));
    }

    [Fact]
    public void BuildUrl_embeds_the_token_at_the_webhook_path()
    {
        var url = WebhookUrlBuilder.BuildUrl("http://cove.local/", "SEC");

        Assert.Equal(
            "http://cove.local/api/extensions/com.alextomas955.whisparrsync/webhook?token=SEC", url);
    }

    [Fact]
    public async Task RegisterWebhook_posts_a_well_formed_v3_notification_payload()
    {
        var handler = FakeHttpMessageHandler.Json("{\"id\":1}");
        var adapter = new V3Adapter(new WhisparrClient(new HttpClient(handler)));

        var result = await adapter.RegisterWebhookAsync(
            "http://localhost:6969", "key", WebhookUrl, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("Webhook", handler.LastRequestBody!, StringComparison.Ordinal);        // implementation
        Assert.Contains("WebhookSettings", handler.LastRequestBody, StringComparison.Ordinal);  // configContract
        Assert.Contains(WebhookUrl, handler.LastRequestBody, StringComparison.Ordinal);         // fields[].value url
    }

    [Fact]
    public async Task RegisterWebhook_is_best_effort_a_non_2xx_is_not_ok()
    {
        var handler = FakeHttpMessageHandler.Status(HttpStatusCode.BadRequest);
        var adapter = new V3Adapter(new WhisparrClient(new HttpClient(handler)));

        var result = await adapter.RegisterWebhookAsync(
            "http://localhost:6969", "key", WebhookUrl, CancellationToken.None);

        Assert.False(result.IsOk); // non-2xx → registered:false fallback, never a throw
    }
}
