namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// Whisparr v2 (Sonarr-based) wire-body fixtures as verbatim string blobs so the v2 client + adapter are
/// tested against the real shape, not a re-serialized DTO (the <c>WebhookPayloads.cs</c> convention). The
/// <see cref="SeriesArray"/> and the first <see cref="EpisodesSeries1"/> row are captured live from the
/// seeded Vixen studio on the v2 e2e instance (:6970, <c>seriesId=1</c>, <c>tvdbId=3372</c>, version
/// 2.2.0.108); the two <c>hasFile:true</c> episodes and the <see cref="EpisodeFilesSeries1"/> rows are
/// constructed faithfully from the same shape (the live seed has no downloaded files, so episodefile is
/// empty there — see 04-RESEARCH.md §"The scene identity crux" for the authoritative key set).
///
/// Note the identity crux VERIFIED live: NO <c>stashId</c>/<c>foreignId</c>/<c>imdbId</c> on any row —
/// the only scene identity is <c>tvdbId</c> (a TPDB scene id). These fixtures deliberately omit those keys
/// so the synth's StashDB-leg no-op is proven against the real absence.
/// </summary>
internal static class V2Fixtures
{
    /// <summary>Live-captured <c>GET /api/v3/series</c> for the seeded Vixen studio (one row, seriesId=1).</summary>
    public const string SeriesArray = """
        [
          {
            "id": 1,
            "tvdbId": 3372,
            "title": "Vixen",
            "titleSlug": "vixen",
            "path": "/config/media/Vixen",
            "sortTitle": "vixen",
            "status": "continuing",
            "network": "Vixen Media Group",
            "monitored": true,
            "seasonFolder": true,
            "year": 0,
            "tags": []
          }
        ]
        """;

    /// <summary>
    /// <c>GET /api/v3/episode?seriesId=1</c>: the first row is verbatim-live (id=1, hasFile:false,
    /// episodeFileId:0 — an undownloaded scene → MovieFile null); the next two are constructed
    /// hasFile:true scenes whose episodeFileId joins to <see cref="EpisodeFilesSeries1"/>.
    /// </summary>
    public const string EpisodesSeries1 = """
        [
          {
            "id": 1,
            "seriesId": 1,
            "tvdbId": 1010276,
            "episodeFileId": 0,
            "seasonNumber": 2016,
            "title": "Payment Extension",
            "releaseDate": "2016-06-13",
            "runtime": 43,
            "overview": "This stunning babe is Ariana.",
            "hasFile": false,
            "monitored": true,
            "actors": [ { "tpdbId": 83401, "name": "Ariana Marie" } ]
          },
          {
            "id": 2,
            "seriesId": 1,
            "tvdbId": 1010277,
            "episodeFileId": 5001,
            "seasonNumber": 2017,
            "title": "Second Scene",
            "releaseDate": "2017-04-20",
            "runtime": 41,
            "overview": "A downloaded scene.",
            "hasFile": true,
            "monitored": true,
            "actors": []
          },
          {
            "id": 3,
            "seriesId": 1,
            "tvdbId": 1010278,
            "episodeFileId": 5002,
            "seasonNumber": 2018,
            "title": "Third Scene",
            "releaseDate": "2018-09-05",
            "runtime": 38,
            "overview": "Another downloaded scene.",
            "hasFile": true,
            "monitored": false,
            "actors": []
          }
        ]
        """;

    /// <summary>
    /// <c>GET /api/v3/episodefile?seriesId=1</c>: the on-disk files whose <c>id</c> joins back to the
    /// <c>episodeFileId</c> of the two <c>hasFile:true</c> episodes above (path is the path-leg source).
    /// </summary>
    public const string EpisodeFilesSeries1 = """
        [
          {
            "id": 5001,
            "seriesId": 1,
            "seasonNumber": 2017,
            "path": "/config/media/Vixen/second-scene.mkv",
            "size": 123456789,
            "relativePath": "second-scene.mkv"
          },
          {
            "id": 5002,
            "seriesId": 1,
            "seasonNumber": 2018,
            "path": "/config/media/Vixen/third-scene.mkv",
            "size": 987654321,
            "relativePath": "third-scene.mkv"
          }
        ]
        """;

    /// <summary>An empty JSON array (the live seed's episodefile shape — no downloaded files).</summary>
    public const string EmptyArray = "[]";
}
