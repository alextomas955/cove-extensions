using System.Security.Cryptography;

namespace WhisparrSync.Webhook;

/// <summary>
/// Mints the webhook secret and builds the copy-paste webhook URL Whisparr posts events to (CONN-07). The
/// secret is a high-entropy token generated with <see cref="System.Security.Cryptography.RandomNumberGenerator"/> (never
/// <c>System.Random</c> — Security §V6) and persisted once in <c>WhisparrOptions.WebhookSecret</c> so the
/// URL is stable across calls. The receiver endpoint that consumes this URL is Phase 3 — this phase only
/// generates and (best-effort) registers it.
/// </summary>
internal static class WebhookUrlBuilder
{
    /// <summary>The extension's inbound webhook path (the receiver lands in Phase 3).</summary>
    internal const string WebhookPath = "/api/extensions/com.alextomas955.whisparrsync/webhook";

    private const int SecretByteLength = 32; // 256 bits of entropy

    /// <summary>Mints a fresh URL-safe (base64url, unpadded) high-entropy secret via <see cref="RandomNumberGenerator"/>.</summary>
    internal static string MintSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(SecretByteLength);
        // URL-safe base64 (RFC 4648 §5), padding stripped, so the token is safe as a query value.
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Returns the existing secret when one is stored (a stable URL across calls), otherwise mints a fresh one.
    /// </summary>
    internal static string EnsureSecret(string? existing)
        => string.IsNullOrEmpty(existing) ? MintSecret() : existing;

    /// <summary>
    /// Builds the copy-paste webhook URL against the Cove host base + the extension's webhook path, with the
    /// secret as a query token: <c>{coveBaseUrl}{WebhookPath}?token={secret}</c>.
    /// </summary>
    internal static string BuildUrl(string coveBaseUrl, string secret)
        => $"{coveBaseUrl.TrimEnd('/')}{WebhookPath}?token={Uri.EscapeDataString(secret)}";
}
