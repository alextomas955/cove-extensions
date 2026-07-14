namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// Whisparr webhook body fixtures as verbatim string blobs so the receiver is tested against the real
/// wire shape (not a re-serialized DTO). The <see cref="Test"/> body was captured live against
/// whisparr-e2e v3.3.4.794; the <see cref="Download"/> family is the Radarr-family On-Import contract
/// (v3 is Radarr-based) — see 03-RESEARCH.md "Code Examples".
/// </summary>
internal static class WebhookPayloads
{
    /// <summary>The live-captured <c>eventType:"Test"</c> pre-save ping (must answer 200 with no ingest).</summary>
    public const string Test = """
        {
          "movie": { "id": 1, "title": "Test Title", "year": 1970, "releaseDate": "1970-01-01",
                     "folderPath": "C:\\testpath", "tmdbId": 0, "tags": ["test-tag"] },
          "remoteMovie": { "tmdbId": 1234, "imdbId": "5678", "title": "Test title", "year": 1970 },
          "release": { "quality": "Test Quality", "qualityVersion": 1, "releaseGroup": "Test Group",
                       "releaseTitle": "Test Title", "indexer": "Test Indexer", "size": 9999999, "customFormatScore": 0 },
          "eventType": "Test", "instanceName": "Whisparr", "applicationUrl": ""
        }
        """;

    /// <summary>
    /// An On-Import (<c>eventType:"Download"</c>) body whose <c>movieFile.path</c> is <paramref name="path"/>.
    /// <paramref name="downloadId"/> and <paramref name="isUpgrade"/> drive the ledger key + upgrade-in-place path.
    /// </summary>
    public static string Download(string path, string downloadId = "ABCDEF0123", bool isUpgrade = false, int movieId = 1, int movieFileId = 42)
        => $$"""
        {
          "movie": { "id": {{movieId}}, "folderPath": "/data/media", "tmdbId": 0 },
          "remoteMovie": { "tmdbId": 0 },
          "movieFile": { "id": {{movieFileId}}, "relativePath": "Scene.mkv", "path": {{Json(path)}},
                         "quality": "WEBDL-1080p", "size": 123456789 },
          "isUpgrade": {{(isUpgrade ? "true" : "false")}},
          "downloadId": "{{downloadId}}",
          "downloadClient": "qBittorrent",
          "eventType": "Download",
          "instanceName": "Whisparr"
        }
        """;

    /// <summary>A valid-token body with an <paramref name="eventType"/> the receiver does not act on (ignored → 200).</summary>
    public static string WithEventType(string eventType)
        => $$"""
        { "eventType": "{{eventType}}", "instanceName": "Whisparr" }
        """;

    // Emit a JSON string literal with the path's backslashes/quotes escaped so a Windows-style path stays valid JSON.
    private static string Json(string value)
        => System.Text.Json.JsonSerializer.Serialize(value);
}
