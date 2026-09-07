using System.Globalization;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;

namespace WhisparrSync.Tests.Adapters;

/// <summary>
/// The live Whisparr v2 end-to-end confirmation. Unlike every other
/// test in this suite — which fakes the outbound boundary with <c>FakeHttpMessageHandler</c> — these facts
/// talk to a REAL running v2 instance over a real <see cref="HttpClient"/>. They are therefore
/// <see cref="FactAttribute">Fact</see>s gated on two environment variables:
/// <c>WHISPARR_V2_E2E_URL</c> and <c>WHISPARR_V2_E2E_KEY</c> (the short <c>WHISPARR_V2_URL</c>/
/// <c>WHISPARR_V2_KEY</c> aliases are also honored). When either is absent the facts SKIP WITH A VISIBLE
/// REASON so the default CI run stays green and never depends on a live instance.
/// </summary>
/// <remarks>
/// SECURITY: the live API key is read from the environment at runtime and used only to build the
/// <c>X-Api-Key</c> header on the outbound call. It is NEVER hardcoded into this source, captured into a
/// committed fixture, or written to an assertion message / log — a failing assertion reports only the
/// non-secret shape it observed (state, counts, event types, key names), never the credential.
///
/// Run it against the seeded Vixen studio on the e2e instance:
/// <code>
/// WHISPARR_V2_E2E_URL=http://localhost:6970 WHISPARR_V2_E2E_KEY=&lt;key&gt; \
///   dotnet test extensions/WhisparrSync/src/WhisparrSync.Tests/WhisparrSync.Tests.csproj \
///   --filter FullyQualifiedName~V2LiveE2E
/// </code>
/// </remarks>
[Trait("Tier", "L3")]
public sealed class V2LiveE2ETests
{
    // The history-import contract <c>ReconcileJob</c> depends on: the single import eventType
    // and the data-map keys the reconcile reads. Mirrored here as literals so this test confirms the exact
    // wire contract ReconcileJob depends on against the live instance.
    private const string ImportEventType = "downloadFolderImported";
    private static readonly string[] PathKeys = ["importedPath", "droppedPath"];
    private const string DownloadIdKey = "downloadId";

    // How many newest history pages to scan for a live import row before giving up (and skipping the fact).
    private const int HistoryPagesToScan = 5;
    private const int HistoryPageSize = 50;

    /// <summary>
    /// Reads the live v2 URL + key from the environment (primary <c>WHISPARR_V2_E2E_*</c> names, then the
    /// <c>WHISPARR_V2_*</c> aliases). <see cref="Assert.Skip"/> when either is absent, so a fact gated on this
    /// helper skips WITH a reason rather than failing on a bare CI runner. The key is returned, never logged.
    /// </summary>
    private static (WhisparrClient Client, string BaseUrl, string ApiKey) LiveClientOrSkip()
    {
        var baseUrl = Env("WHISPARR_V2_E2E_URL", "WHISPARR_V2_URL");
        var apiKey = Env("WHISPARR_V2_E2E_KEY", "WHISPARR_V2_KEY");

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            Assert.Skip("live v2 gate not set — export WHISPARR_V2_E2E_URL + WHISPARR_V2_E2E_KEY to run the live v2 E2E");
        }

        // A real HttpClient (not a fake handler): these facts exercise the genuine transport path.
        return (new WhisparrClient(new HttpClient()), baseUrl!, apiKey!);
    }

    private static string? Env(string primary, string alias)
    {
        var value = Environment.GetEnvironmentVariable(primary);
        return string.IsNullOrWhiteSpace(value) ? Environment.GetEnvironmentVariable(alias) : value;
    }

    /// <summary>
    /// A live <c>GET /api/v3/system/status</c> is Ok, reports a major==2 version, and
    /// <see cref="AdapterSelector.Select"/> routes it to a <see cref="V2Adapter"/> (not a version mismatch).
    /// </summary>
    [Fact]
    public async Task Connect_LiveV2_ReportsMajor2_AndSelectsV2Adapter()
    {
        var (client, baseUrl, apiKey) = LiveClientOrSkip();

        var status = await client.GetStatusAsync(baseUrl, apiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, status.State);
        Assert.Equal(2, AdapterSelector.ParseMajor(status.Value!.Version));

        var adapter = AdapterSelector.Select(status.Value!, client);
        Assert.IsType<V2Adapter>(adapter);
    }

    /// <summary>
    /// The v2 scene-enumeration remap over the live seed: <see cref="V2Adapter.ListMoviesAsync"/> returns Ok
    /// with at least one row; EVERY row upholds the v2 identity guard (<c>StashId == null</c>,
    /// <c>ItemType == "v2scene"</c>) so the StashDB matcher leg no-ops; and every downloaded (<c>HasFile</c>)
    /// row carries a non-empty <c>MovieFile.Path</c> — the path source the import/match legs read.
    /// </summary>
    [Fact]
    public async Task Enumerate_LiveV2_SynthesizesScenes_WithV2SceneGuard_AndPathsForDownloaded()
    {
        var (client, baseUrl, apiKey) = LiveClientOrSkip();
        var adapter = new V2Adapter(client);

        var result = await adapter.ListMoviesAsync(baseUrl, apiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.NotEmpty(result.Value!);

        Assert.All(result.Value!, movie =>
        {
            Assert.Null(movie.StashId);
            Assert.Equal("v2scene", movie.ItemType);
            if (movie.HasFile)
            {
                Assert.NotNull(movie.MovieFile);
                Assert.False(string.IsNullOrWhiteSpace(movie.MovieFile!.Path));
            }
        });
    }

    /// <summary>
    /// Scans the newest live history pages for a <c>downloadFolderImported</c>
    /// record and confirms its data map carries the exact keys <c>ReconcileJob</c> reads — an imported
    /// path (<c>importedPath</c> or <c>droppedPath</c>) and <c>downloadId</c>. When the seed has no import row
    /// yet (no file has been grabbed/imported), the fact SKIPS WITH A REASON so the gap is explicit rather
    /// than a false pass — fire a grab or a manual import into the seeded Vixen series to exercise it.
    /// </summary>
    [Fact]
    public async Task ImportHistoryKeys_LiveV2_ExposeImportedPathAndDownloadId_OrSkip()
    {
        var (client, baseUrl, apiKey) = LiveClientOrSkip();

        WhisparrHistoryRecord? import = null;
        for (var page = 1; page <= HistoryPagesToScan && import is null; page++)
        {
            var history = await client.ListHistoryAsync(baseUrl, apiKey, page, HistoryPageSize, CancellationToken.None);
            Assert.Equal(WhisparrResultState.Ok, history.State);

            var records = history.Value!.Records;
            if (records is not { Length: > 0 })
            {
                break; // a clean end of history — no more pages to scan
            }

            foreach (var record in records)
            {
                if (string.Equals(record.EventType, ImportEventType, StringComparison.OrdinalIgnoreCase))
                {
                    import = record;
                    break;
                }
            }
        }

        if (import is null)
        {
            Assert.Skip($"no live v2 '{ImportEventType}' history row — fire a grab or manual import into the seeded series to confirm the import keys");
        }

        var data = import!.Data;
        Assert.NotNull(data);

        var importedPath = LookupAny(data!, PathKeys);
        Assert.False(string.IsNullOrWhiteSpace(importedPath), $"expected one of [{string.Join(", ", PathKeys)}] in the import data map");
        Assert.True(HasKey(data!, DownloadIdKey), $"expected '{DownloadIdKey}' in the import data map");
    }

    // The now-GO v2 outward capabilities: a Cove studio maps to a v2 SITE (series) keyed on the TPDB id, so
    // these GO loop-safely (add is non-grabbing, only the episode search grabs). Documentary — the offline
    // V2OutwardParityTests prove each; the read-only live fact below confirms the resolve path end-to-end.
    private static readonly string[] GoOutwardCapabilities =
        ["studio-site monitor", "studio-site status", "attributed episode ids", "episode search"];

    /// <summary>
    /// The GO-outward live confirmation, READ-ONLY by design: discovers a seeded v2 site from
    /// <c>GET /series</c> and confirms the GO resolve path — status (added + grabbed-of-total) and the
    /// attributed episode-id enumeration — resolves it by its TPDB id against a real instance. It performs NO
    /// mutation (no add/monitor/search), so it never grabs and leaves the seed untouched; the mutating GO
    /// paths' loop-safety is proven offline by <c>V2OutwardParityTests</c> and <c>V2AdapterTests</c>. SKIPS
    /// WITH A REASON when the live gate is unset or the seed has no added site with a TPDB id.
    /// </summary>
    [Fact]
    public async Task GoOutward_LiveV2_ResolvesSeededSite_StatusAndAttributedIds_ReadOnly()
    {
        Assert.NotEmpty(GoOutwardCapabilities);

        var (client, baseUrl, apiKey) = LiveClientOrSkip();
        var adapter = new V2Adapter(client);

        var series = await client.ListSeriesAsync(baseUrl, apiKey, CancellationToken.None);
        Assert.Equal(WhisparrResultState.Ok, series.State);

        var seeded = Array.Find(series.Value!, s => s.TvdbId is > 0);
        if (seeded is null)
        {
            Assert.Skip("no added v2 site with a TPDB id on the live seed — add one (e.g. the seeded Vixen studio) to confirm the GO outward resolve path");
        }

        var tpdbId = seeded!.TvdbId!.Value.ToString(CultureInfo.InvariantCulture);

        var status = await adapter.GetStudioStatusAsync(baseUrl, apiKey, tpdbId, CancellationToken.None);
        Assert.Equal(WhisparrResultState.Ok, status.State);
        Assert.True(status.Value!.Added);
        Assert.True(status.Value.ScenesPresent <= status.Value.ScenesTotal);

        var ids = await adapter.ListStudioAttributedIdsAsync(
            baseUrl, apiKey, tpdbId, monitoredOnly: false, CancellationToken.None);
        Assert.Equal(WhisparrResultState.Ok, ids.State);
    }

    // Case-insensitive first-non-blank lookup over the history data map (Whisparr emits camelCase, but a
    // dictionary key binds verbatim — match tolerantly, exactly as ReconcileJob.ValueOf does).
    private static string? LookupAny(IReadOnlyDictionary<string, string> data, string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var pair in data)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    return pair.Value;
                }
            }
        }

        return null;
    }

    private static bool HasKey(IReadOnlyDictionary<string, string> data, string key)
    {
        foreach (var pair in data)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                return true;
            }
        }

        return false;
    }

    // The end-to-end confirmation of the no-refresh-on-v2 decision against a REAL v2 instance: monitor a
    // THROWAWAY site under NewReleases and confirm the discovered back-catalogue is NOT left all-monitored (the
    // add body's monitor:"none" lever, not a refresh, keeps the episodes unmonitored — no flood). Gated on the
    // live env AND an explicit disposable-site TPDB id so the default CI run never mutates a live seed; SKIPS
    // WITH A REASON when either is absent or the site has no episodes yet. The API key is read from the env and
    // used only for the X-Api-Key header — never logged or asserted on.
    [Fact]
    public async Task StudioMonitor_LiveV2_NewReleases_LeavesBackCatalogueUnmonitored_OrSkip()
    {
        var baseUrl = Env("WHISPARR_V2_E2E_URL", "WHISPARR_V2_URL");
        var apiKey = Env("WHISPARR_V2_E2E_KEY", "WHISPARR_V2_KEY");
        var disposableTpdb = Environment.GetEnvironmentVariable("WHISPARR_V2_E2E_DISPOSABLE_TPDB");

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            Assert.Skip("live v2 gate not set — export WHISPARR_V2_E2E_URL + WHISPARR_V2_E2E_KEY to run the live v2 probe");
        }
        if (string.IsNullOrWhiteSpace(disposableTpdb))
        {
            Assert.Skip("no disposable site — export WHISPARR_V2_E2E_DISPOSABLE_TPDB=<throwaway site tpdb id> to run the mutating v2 NewReleases probe");
        }

        var client = new WhisparrClient(new HttpClient());
        var adapter = new V2Adapter(client);

        var monitor = await adapter.SetStudioMonitorAsync(
            baseUrl!, apiKey!, disposableTpdb!, monitored: true,
            MonitorScope.NewReleases, rootFolderPath: "/config/media", qualityProfileId: 1, tagIds: [], CancellationToken.None);
        Assert.Equal(WhisparrResultState.Ok, monitor.State);

        var series = await client.ListSeriesAsync(baseUrl!, apiKey!, CancellationToken.None);
        Assert.Equal(WhisparrResultState.Ok, series.State);
        var site = Array.Find(
            series.Value!,
            s => s.TvdbId is { } id && id.ToString(CultureInfo.InvariantCulture) == disposableTpdb);
        Assert.NotNull(site);

        var episodes = await client.ListEpisodesAsync(baseUrl!, apiKey!, site!.Id, CancellationToken.None);
        Assert.Equal(WhisparrResultState.Ok, episodes.State);
        if (episodes.Value!.Length == 0)
        {
            Assert.Skip("the disposable site has no episodes yet (Whisparr fetches them asynchronously) — re-run once the catalogue populated");
        }

        // NewReleases leaves the back-catalogue unmonitored, so NOT every episode is monitored. An all-monitored
        // set would be the mass-grab-armed flood the monitor design forbids.
        Assert.Contains(episodes.Value!, e => !e.Monitored);
    }
}
