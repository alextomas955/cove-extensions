namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// Whisparr v2 (Sonarr-based) wire-body fixtures as verbatim string blobs so the v2 client + adapter are
/// tested against the real shape, not a re-serialized DTO (the <c>WebhookPayloads.cs</c> convention). The
/// <see cref="SeriesArray"/> and the first <see cref="EpisodesSeries1"/> row are captured live from the
/// seeded Vixen studio on the v2 e2e instance (:6970, <c>seriesId=1</c>, <c>tvdbId=3372</c>, version
/// 2.2.0.108); the two <c>hasFile:true</c> episodes and the <see cref="EpisodeFilesSeries1"/> rows are
/// constructed faithfully from the same shape (the live seed has no downloaded files, so episodefile is
/// empty there — v2 scenes carry no StashDB-comparable identity).
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

    /// <summary>
    /// Live-captured <c>GET /api/v3/series/lookup?term=tpdb:3372</c> — one monitorable site row. The site
    /// identity is <c>tvdbId</c> (the TPDB site id); a lookup row carries NO <c>id</c> (not added yet). The
    /// blob is trimmed to one image + one season for readability; every top-level field the DTO binds is
    /// verbatim from the live 2.2.0.108 instance.
    /// </summary>
    public const string SeriesLookup = """
        [
          {
            "title": "Tushy",
            "sortTitle": "tushy",
            "status": "continuing",
            "ended": false,
            "overview": "Tushy is a part of the Vixen Media Group network.",
            "network": "Vixen Media Group",
            "images": [
              { "coverType": "poster", "url": "/MediaCoverProxy/abc/tushy-poster.jpg", "remoteUrl": "https://cdn.theporndb.net/sites/tushy-poster.jpg" }
            ],
            "originalLanguage": { "id": 1, "name": "English" },
            "remotePoster": "https://cdn.theporndb.net/sites/tushy-poster.jpg",
            "seasons": [ { "seasonNumber": 2015, "monitored": true } ],
            "year": 2015,
            "qualityProfileId": 0,
            "monitored": true,
            "seriesType": "standard",
            "monitorNewItems": "all",
            "useSceneNumbering": false,
            "runtime": 40,
            "tvdbId": 3417,
            "cleanTitle": "tushy",
            "titleSlug": "tushy",
            "folder": "Tushy",
            "genres": [],
            "tags": [],
            "added": "0001-01-01T00:00:00Z",
            "ratings": { "votes": 0, "value": 0 },
            "statistics": { "seasonCount": 12, "episodeFileCount": 0, "episodeCount": 0, "totalEpisodeCount": 0, "sizeOnDisk": 0, "percentOfEpisodes": 0 }
          }
        ]
        """;

    /// <summary>
    /// Live-captured <c>POST /api/v3/series</c> response (HTTP 201) for a non-grabbing add. The created row
    /// carries the assigned <c>id</c>; top-level <c>monitored</c> reflects <c>addOptions.monitor</c> (with
    /// <c>monitor:"none"</c> the created site is <c>monitored:false</c>). The <c>addOptions</c> echo shows
    /// the non-grab keys the request sent — <c>searchForMissingEpisodes:false</c> is the loop-safety key.
    /// </summary>
    public const string SeriesAddResponse = """
        {
          "title": "Tushy",
          "sortTitle": "tushy",
          "status": "continuing",
          "ended": false,
          "overview": "Tushy is a part of the Vixen Media Group network.",
          "network": "Vixen Media Group",
          "images": [ { "coverType": "poster", "url": "/MediaCover/3/poster.jpg", "remoteUrl": "https://cdn.theporndb.net/sites/tushy-poster.jpg" } ],
          "originalLanguage": { "id": 1, "name": "English" },
          "seasons": [ { "seasonNumber": 2015, "monitored": true } ],
          "year": 2015,
          "path": "/config/media/Tushy",
          "qualityProfileId": 1,
          "monitored": false,
          "seriesType": "standard",
          "monitorNewItems": "none",
          "useSceneNumbering": false,
          "runtime": 40,
          "tvdbId": 3417,
          "cleanTitle": "tushy",
          "titleSlug": "tushy",
          "rootFolderPath": "/config/media",
          "genres": [],
          "tags": [],
          "added": "2026-07-15T12:02:31Z",
          "addOptions": { "searchForMissingEpisodes": false, "searchForCutoffUnmetEpisodes": false, "ignoreEpisodesWithFiles": false, "ignoreEpisodesWithoutFiles": false, "episodesToMonitor": [], "monitor": "none" },
          "ratings": { "votes": 0, "value": 0 },
          "statistics": { "seasonCount": 12, "episodeFileCount": 0, "episodeCount": 0, "totalEpisodeCount": 0, "sizeOnDisk": 0, "percentOfEpisodes": 0 },
          "id": 3
        }
        """;

    /// <summary>
    /// Live-captured <c>PUT /api/v3/series/{id}</c> monitor-flip response (HTTP 202): the resource echoed
    /// back with <c>monitored:true</c>. Same shape as the add response; the flip toggles <c>monitored</c>.
    /// </summary>
    public const string SeriesPutResponse = """
        {
          "title": "Tushy",
          "sortTitle": "tushy",
          "status": "continuing",
          "ended": false,
          "network": "Vixen Media Group",
          "seasons": [ { "seasonNumber": 2015, "monitored": true } ],
          "year": 2015,
          "path": "/config/media/Tushy",
          "qualityProfileId": 1,
          "monitored": true,
          "monitorNewItems": "none",
          "tvdbId": 3417,
          "titleSlug": "tushy",
          "rootFolderPath": "/config/media",
          "tags": [],
          "id": 3
        }
        """;

    /// <summary>
    /// Live-captured <c>POST /api/v3/series</c> DUPLICATE-add body (HTTP 400): v2 signals an already-added
    /// site with a <c>SeriesExistsValidator</c> validation body, NOT a 409 — the v2 analogue of v3's
    /// <c>MovieExistsValidator</c>. The create verb classifies this as the same non-error Conflict a 409 yields.
    /// </summary>
    public const string SeriesExistsError = """
        [
          {
            "propertyName": "TvdbId",
            "errorMessage": "This series has already been added",
            "attemptedValue": 3417,
            "severity": "error",
            "errorCode": "SeriesExistsValidator"
          }
        ]
        """;

    /// <summary>Live-captured <c>POST /api/v3/command</c> response for <c>SeriesSearch{seriesId}</c> (HTTP 201).</summary>
    public const string SeriesSearchCommandResponse = """
        { "name": "SeriesSearch", "commandName": "Series Search", "status": "started", "id": 4070 }
        """;

    /// <summary>Live-captured <c>POST /api/v3/command</c> response for <c>EpisodeSearch{episodeIds[]}</c> (HTTP 201).</summary>
    public const string EpisodeSearchCommandResponse = """
        { "name": "EpisodeSearch", "commandName": "Episode Search", "status": "queued", "id": 4071 }
        """;
}
