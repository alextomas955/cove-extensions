using System.Globalization;
using System.Text.Json;
using WhisparrSync.Client;

namespace WhisparrSync.Adapters;

/// <summary>
/// The Whisparr v2 (Sonarr-based "v2" branch) adapter (VER-03): implements the same
/// <see cref="IWhisparrAdapter"/> port as <see cref="V3Adapter"/>, but over v2's content model. The five
/// connect-level calls (status/rootfolder/qualityprofile/history/register) are byte-identical envelopes on
/// v2 (VERIFIED live, 04-RESEARCH.md §Endpoint shape parity), so they delegate to the shared transport-only
/// <see cref="WhisparrClient"/> unchanged. The ONE substantive method is <see cref="ListMoviesAsync"/>: v2
/// has no <c>/movie</c> entity, so it walks <c>series → episode → episodefile</c> and synthesizes the
/// normalized <c>WhisparrMovie[]</c> the reused <c>IdentityMatcher</c> already understands.
/// </summary>
/// <remarks>
/// The notification-payload helpers (<see cref="BuildNotificationPayload"/> / <see cref="ExtractToken"/>)
/// are duplicated from <see cref="V3Adapter"/> rather than lifted to a shared type: v2's <c>/notification</c>
/// toggle set is a superset of v3's (<c>onDownload</c>/<c>onUpgrade</c>/<c>onRename</c> all valid — VERIFIED,
/// 04-RESEARCH.md §notification), so the payload is identical. Duplication keeps this plan additive — it does
/// not touch <see cref="V3Adapter"/> at all (V3Adapter.cs is out of this plan's file set), avoiding any
/// regression risk to the shipped, live-verified v3 path.
/// </remarks>
internal sealed class V2Adapter(WhisparrClient client) : IWhisparrAdapter
{
    public Task<WhisparrResult<SystemStatus>> GetStatusAsync(string baseUrl, string apiKey, CancellationToken ct)
        => client.GetStatusAsync(baseUrl, apiKey, ct);

    public Task<WhisparrResult<RootFolder[]>> ListRootFoldersAsync(string baseUrl, string apiKey, CancellationToken ct)
        => client.ListRootFoldersAsync(baseUrl, apiKey, ct);

    public Task<WhisparrResult<QualityProfile[]>> ListQualityProfilesAsync(string baseUrl, string apiKey, CancellationToken ct)
        => client.ListQualityProfilesAsync(baseUrl, apiKey, ct);

    public Task<WhisparrResult<WhisparrHistoryPage>> ListHistoryAsync(
        string baseUrl, string apiKey, int page, int pageSize, CancellationToken ct)
        => client.ListHistoryAsync(baseUrl, apiKey, page, pageSize, ct);

    public Task<WhisparrResult<bool>> RegisterWebhookAsync(string baseUrl, string apiKey, string webhookUrl, CancellationToken ct)
        => client.RegisterWebhookAsync(baseUrl, apiKey, BuildNotificationPayload(webhookUrl), ct);

    /// <summary>
    /// The v2 scene-enumeration remap (the reconciliation data source): <c>GET /series</c>, then per series
    /// <c>GET /episode?seriesId</c> + <c>GET /episodefile?seriesId</c>, synthesizing one
    /// <see cref="WhisparrMovie"/> per episode. CRITICAL (Pitfall 1, 04-RESEARCH.md): <c>StashId</c> is
    /// null and <c>ItemType</c> is <c>"v2scene"</c> (never <c>"scene"</c>) so <c>IdentityMatcher.StashMatches</c>
    /// no-ops for v2 rows — a TPDB id is never compared to a Cove StashDB UUID. Fail-safe: if the series read
    /// (or any per-series episode/episodefile read) is not Ok, the same-state result propagates — no partial synth.
    /// </summary>
    public async Task<WhisparrResult<WhisparrMovie[]>> ListMoviesAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        var seriesResult = await client.ListSeriesAsync(baseUrl, apiKey, ct);
        if (!seriesResult.IsOk)
        {
            return Propagate<WhisparrSeries[], WhisparrMovie[]>(seriesResult);
        }

        var movies = new List<WhisparrMovie>();
        foreach (var series in seriesResult.Value!)
        {
            var episodesResult = await client.ListEpisodesAsync(baseUrl, apiKey, series.Id, ct);
            if (!episodesResult.IsOk)
            {
                return Propagate<WhisparrEpisode[], WhisparrMovie[]>(episodesResult);
            }

            var filesResult = await client.ListEpisodeFilesAsync(baseUrl, apiKey, series.Id, ct);
            if (!filesResult.IsOk)
            {
                return Propagate<WhisparrEpisodeFile[], WhisparrMovie[]>(filesResult);
            }

            // Join episode.episodeFileId -> episodefile.path. Last row wins on a duplicate id (Whisparr
            // returns one file per id), and an episode whose file is absent yields MovieFile = null.
            var pathByFileId = new Dictionary<int, string?>();
            foreach (var file in filesResult.Value!)
            {
                pathByFileId[file.Id] = file.Path;
            }

            foreach (var episode in episodesResult.Value!)
            {
                WhisparrMovieFile? movieFile = null;
                if (episode.EpisodeFileId != 0 && pathByFileId.TryGetValue(episode.EpisodeFileId, out var path))
                {
                    movieFile = new WhisparrMovieFile(episode.EpisodeFileId, path);
                }

                movies.Add(new WhisparrMovie(
                    Id: episode.Id,
                    Title: episode.Title,
                    Year: ParseYear(episode.ReleaseDate),
                    StashId: null,
                    ForeignId: episode.TvdbId?.ToString(CultureInfo.InvariantCulture),
                    ItemType: "v2scene",
                    Monitored: episode.Monitored,
                    HasFile: episode.HasFile,
                    MovieFile: movieFile));
            }
        }

        return WhisparrResult<WhisparrMovie[]>.Ok([.. movies]);
    }

    // Take the leading 4-digit year of an ISO-ish releaseDate ("2016-06-13" -> 2016); null on absence or
    // a non-numeric leading segment (defensive — a partial/odd row must never throw the whole synth).
    private static int? ParseYear(string? releaseDate)
    {
        if (string.IsNullOrWhiteSpace(releaseDate) || releaseDate.Length < 4)
        {
            return null;
        }

        return int.TryParse(releaseDate.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
    }

    // Re-shape a non-Ok result of one payload type into the same state for another — the synth propagates a
    // failed read verbatim (state + diagnostic) rather than inventing a partial success.
    private static WhisparrResult<TTo> Propagate<TFrom, TTo>(WhisparrResult<TFrom> source)
        => source.State switch
        {
            WhisparrResultState.BadKey => WhisparrResult<TTo>.BadKey(),
            WhisparrResultState.NotWhisparr => WhisparrResult<TTo>.NotWhisparr(),
            WhisparrResultState.VersionMismatch => WhisparrResult<TTo>.VersionMismatch(source.DetectedVersion ?? string.Empty),
            _ => WhisparrResult<TTo>.Unreachable(source.Reason ?? "unreachable"),
        };

    // The v2 Webhook connection payload — identical to v3's (the toggle set is a superset on v2). See the
    // class <remarks> for why this is duplicated rather than shared. `method` value 1 = POST; the secret is
    // delivered as the `X-Cove-Token` header so the receiver (03-01) authenticates the Test ping.
    internal static string BuildNotificationPayload(string webhookUrl)
        => JsonSerializer.Serialize(new
        {
            name = "Cove Whisparr Sync",
            implementation = "Webhook",
            implementationName = "Webhook",
            configContract = "WebhookSettings",
            onGrab = false,
            onDownload = true,
            onUpgrade = true,
            onRename = true,
            fields = new object[]
            {
                new { name = "url", value = webhookUrl },
                new { name = "method", value = 1 },
                new { name = "headers", value = new object[] { new { key = "X-Cove-Token", value = ExtractToken(webhookUrl) } } },
            },
        });

    // Lift the `?token=` query value back out so the header carries the identical secret the receiver
    // validates. Returns empty when no token query is present.
    private static string ExtractToken(string webhookUrl)
    {
        const string marker = "?token=";
        var start = webhookUrl.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        var raw = webhookUrl[(start + marker.Length)..];
        var amp = raw.IndexOf('&');
        if (amp >= 0)
        {
            raw = raw[..amp];
        }

        return Uri.UnescapeDataString(raw);
    }
}
