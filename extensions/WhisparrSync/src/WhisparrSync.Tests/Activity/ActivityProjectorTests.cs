using System.Text.Json;
using WhisparrSync.Activity;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using static WhisparrSync.Contracts.WireSerializers;

namespace WhisparrSync.Tests.Activity;

/// <summary>
/// The pure History-projection contract. Every test drives <see cref="ActivityProjector.HistoryPage"/> with a
/// plain in-memory <see cref="WhisparrHistoryPage"/> — the projector takes no client/DB/store handle, so
/// "makes no outbound call" is STRUCTURAL here, not mocked. Proves the eventType → event mapping (import /
/// grab / fail), that an unrecognized eventType is omitted, that a null/empty page projects to an EMPTY result
/// (never an error), and that the projected event serializes to its pinned camelCase wire string under the
/// same options the endpoint uses — the exact tokens the FE label map keys on.
/// </summary>
[Trait("Tier", "L0")]
public sealed class ActivityProjectorTests
{
    private static WhisparrHistoryRecord Record(int id, string? eventType, string? title = "Scene", string? date = "2026-07-23T10:00:00Z")
        => new(id, MovieId: id, Date: date, EventType: eventType, Data: null, SourceTitle: title);

    private static WhisparrHistoryPage Page(params WhisparrHistoryRecord[] records)
        => new(Page: 1, PageSize: 50, TotalRecords: records.Length, Records: records);

    [Fact]
    public void DownloadFolderImported_projects_imported_carrying_the_title()
    {
        var result = ActivityProjector.HistoryPage(Page(Record(1, "downloadFolderImported", title: "My Scene")));

        var row = Assert.Single(result.Records);
        Assert.Equal(ActivityHistoryEvent.Imported, row.Event);
        Assert.Equal("My Scene", row.Scene.SceneTitle);
        Assert.Equal("2026-07-23T10:00:00Z", row.Scene.Date);
    }

    [Fact]
    public void Grabbed_projects_grabbed()
    {
        var row = Assert.Single(ActivityProjector.HistoryPage(Page(Record(1, "grabbed"))).Records);
        Assert.Equal(ActivityHistoryEvent.Grabbed, row.Event);
    }

    [Theory]
    [InlineData("downloadFailed")]
    [InlineData("importFailed")]
    public void A_failure_eventType_projects_failed(string eventType)
    {
        var row = Assert.Single(ActivityProjector.HistoryPage(Page(Record(1, eventType))).Records);
        Assert.Equal(ActivityHistoryEvent.Failed, row.Event);
    }

    [Theory]
    [InlineData("movieFileRenamed")]
    [InlineData("downloadIgnored")]
    [InlineData(null)]
    [InlineData("")]
    public void An_unrecognized_eventType_is_omitted(string? eventType)
        => Assert.Empty(ActivityProjector.HistoryPage(Page(Record(1, eventType))).Records);

    [Fact]
    public void Only_recognized_rows_survive_a_mixed_page_preserving_order()
    {
        var result = ActivityProjector.HistoryPage(Page(
            Record(3, "grabbed"),
            Record(2, "movieFileRenamed"),
            Record(1, "downloadFolderImported")));

        Assert.Collection(
            result.Records,
            r => Assert.Equal(ActivityHistoryEvent.Grabbed, r.Event),
            r => Assert.Equal(ActivityHistoryEvent.Imported, r.Event));
    }

    [Fact]
    public void A_null_records_envelope_projects_an_empty_result_not_an_error()
    {
        var result = ActivityProjector.HistoryPage(new WhisparrHistoryPage(Page: 1, PageSize: 50, TotalRecords: 0, Records: null));

        Assert.Empty(result.Records);
        Assert.Equal(1, result.Page);
        Assert.Equal(50, result.PageSize);
    }

    [Fact]
    public void An_empty_records_envelope_projects_an_empty_result()
        => Assert.Empty(ActivityProjector.HistoryPage(Page()).Records);

    [Fact]
    public void A_title_less_row_falls_back_to_a_neutral_placeholder_never_null()
    {
        var row = Assert.Single(ActivityProjector.HistoryPage(Page(Record(1, "grabbed", title: null))).Records);
        Assert.False(string.IsNullOrWhiteSpace(row.Scene.SceneTitle));
    }

    [Theory]
    [InlineData("grabbed", "grabbed")]
    [InlineData("downloadFolderImported", "imported")]
    [InlineData("downloadFailed", "failed")]
    public void The_projected_event_serializes_to_its_camelCase_wire_string(string eventType, string expectedWire)
    {
        var page = ActivityProjector.HistoryPage(Page(Record(1, eventType)));

        var json = JsonSerializer.Serialize(page, EnumStringResponseJsonOptions);
        using var doc = JsonDocument.Parse(json);
        var evt = doc.RootElement.GetProperty("records")[0].GetProperty("event").GetString();

        // The exact token the FE `activityLogic.ts` HISTORY_EVENT_META map keys on — through the options the
        // endpoint serializes with (which carry a plain enum converter), so this also guards the property-level
        // converter that out-ranks it.
        Assert.Equal(expectedWire, evt);
    }

    // ---- Queue projection ----

    private static WhisparrQueueRecord QueueRecord(
        string? status = "downloading",
        string? trackedDownloadState = "downloading",
        string? trackedDownloadStatus = "ok",
        string? title = "A Scene",
        double? size = null,
        double? sizeleft = null,
        string? timeleft = null,
        WhisparrFileQuality? quality = null,
        WhisparrMovie? movie = null,
        WhisparrSeries? series = null)
        => new(
            Id: 1, Title: title, Status: status, TrackedDownloadState: trackedDownloadState,
            TrackedDownloadStatus: trackedDownloadStatus, Size: size, Sizeleft: sizeleft, Timeleft: timeleft,
            EstimatedCompletionTime: null, ErrorMessage: null, Quality: quality, Movie: movie, Series: series);

    private static WhisparrQueuePage QueuePage(params WhisparrQueueRecord[] records)
        => new(Page: 1, PageSize: 50, TotalRecords: records.Length, Records: records);

    [Theory]
    [InlineData("downloading", "downloading", "ok", "Downloading")]
    [InlineData("queued", "downloading", "ok", "Queued")]
    [InlineData("delay", "downloading", "ok", "Queued")]
    [InlineData("paused", "downloading", "ok", "Queued")]
    [InlineData("downloading", "importing", "ok", "Importing")]
    [InlineData("downloading", "importPending", "ok", "Importing")]
    [InlineData("warning", "downloading", "ok", "Warning")]
    [InlineData("downloading", "downloading", "warning", "Warning")]
    [InlineData("failed", "downloading", "ok", "Failed")]
    [InlineData("downloading", "failedPending", "ok", "Failed")]
    [InlineData("downloading", "downloading", "error", "Failed")]
    public void Queue_normalizes_the_status_triple_to_the_five_state_vocabulary(
        string? status, string? state, string? trackedStatus, string expected)
    {
        var row = Assert.Single(ActivityProjector.QueuePage(QueuePage(
            QueueRecord(status: status, trackedDownloadState: state, trackedDownloadStatus: trackedStatus))).Records);
        Assert.Equal(expected, row.State.ToString());
    }

    [Fact]
    public void Queue_failure_outranks_warning_and_importing()
    {
        // trackedDownloadStatus:error is the most actionable signal even while the state still reads importing.
        var row = Assert.Single(ActivityProjector.QueuePage(QueuePage(
            QueueRecord(status: "warning", trackedDownloadState: "importing", trackedDownloadStatus: "error"))).Records);
        Assert.Equal(ActivityQueueState.Failed, row.State);
    }

    [Fact]
    public void Queue_with_size_and_sizeleft_yields_a_determinate_percent()
    {
        var row = Assert.Single(ActivityProjector.QueuePage(QueuePage(
            QueueRecord(size: 1000, sizeleft: 250))).Records);
        Assert.Equal(75, row.ProgressPercent);
    }

    [Theory]
    [InlineData(null, 250d)]
    [InlineData(1000d, null)]
    [InlineData(0d, 0d)]
    public void Queue_missing_or_zero_size_yields_an_indeterminate_null_percent(double? size, double? sizeleft)
    {
        var row = Assert.Single(ActivityProjector.QueuePage(QueuePage(
            QueueRecord(size: size, sizeleft: sizeleft))).Records);
        Assert.Null(row.ProgressPercent);
    }

    [Fact]
    public void Queue_progress_is_clamped_to_0_100()
    {
        // A sizeleft larger than size (a transient Whisparr quirk) must never yield a negative percent.
        var row = Assert.Single(ActivityProjector.QueuePage(QueuePage(
            QueueRecord(size: 1000, sizeleft: 1500))).Records);
        Assert.Equal(0, row.ProgressPercent);
    }

    [Fact]
    public void Queue_reads_title_studio_quality_defensively_v3_movie_row()
    {
        var movie = new WhisparrMovie(
            Id: 9, Title: "Movie Title", Year: 2026, StashId: "s", ForeignId: "s", ItemType: "scene",
            Monitored: true, HasFile: false, MovieFile: null, StudioTitle: "Vixen");
        var quality = new WhisparrFileQuality(new WhisparrQualityName("WEB-DL 1080p"));
        var row = Assert.Single(ActivityProjector.QueuePage(QueuePage(
            QueueRecord(title: "Release.Title", quality: quality, movie: movie))).Records);

        Assert.Equal("Release.Title", row.Scene.SceneTitle);
        Assert.Equal("Vixen", row.Scene.Studio);
        Assert.Equal("WEB-DL 1080p", row.Scene.Quality);
    }

    [Fact]
    public void Queue_v2_series_row_carries_the_site_title_as_studio()
    {
        var series = new WhisparrSeries(Id: 3, TvdbId: 42, Title: "Tushy", TitleSlug: "tushy", Path: "/x");
        var row = Assert.Single(ActivityProjector.QueuePage(QueuePage(
            QueueRecord(title: null, movie: null, series: series))).Records);

        Assert.Equal("Tushy", row.Scene.Studio);
        // Title falls back to the site title when the row carries no release title.
        Assert.Equal("Tushy", row.Scene.SceneTitle);
    }

    [Fact]
    public void Queue_titleless_row_falls_back_to_a_neutral_placeholder_never_null()
    {
        var row = Assert.Single(ActivityProjector.QueuePage(QueuePage(
            QueueRecord(title: null, movie: null, series: null))).Records);
        Assert.False(string.IsNullOrWhiteSpace(row.Scene.SceneTitle));
    }

    [Fact]
    public void Queue_carries_timeleft_as_the_eta()
    {
        var row = Assert.Single(ActivityProjector.QueuePage(QueuePage(QueueRecord(timeleft: "00:12:30"))).Records);
        Assert.Equal("00:12:30", row.Eta);
    }

    [Fact]
    public void A_null_queue_records_envelope_projects_an_empty_result_not_an_error()
    {
        var result = ActivityProjector.QueuePage(new WhisparrQueuePage(Page: 1, PageSize: 50, TotalRecords: 0, Records: null));
        Assert.Empty(result.Records);
        Assert.Equal(1, result.Page);
        Assert.Equal(50, result.PageSize);
    }

    [Fact]
    public void The_projected_queue_state_serializes_to_its_camelCase_wire_string()
    {
        var page = ActivityProjector.QueuePage(QueuePage(QueueRecord(status: "warning")));
        var json = JsonSerializer.Serialize(page, EnumStringResponseJsonOptions);
        using var doc = JsonDocument.Parse(json);
        var state = doc.RootElement.GetProperty("records")[0].GetProperty("state").GetString();

        // The exact token the FE keys on, through the options that carry a plain enum converter — guards the
        // property-level converter that out-ranks it (the SceneStatus/History pitfall).
        Assert.Equal("warning", state);
    }

    // ---- Wanted projection ----

    private static WhisparrMovie MovieRow(
        int id = 1, string? title = "A Scene", bool monitored = true, bool hasFile = false,
        string? studioTitle = null, string? releaseDate = "2026-01-01", string? added = "2026-06-01T00:00:00Z",
        int? seriesId = null, string? itemType = "scene")
        => new(
            Id: id, Title: title, Year: 2026, StashId: "s", ForeignId: "s", ItemType: itemType,
            Monitored: monitored, HasFile: hasFile, MovieFile: null, StudioTitle: studioTitle,
            SeriesId: seriesId, ReleaseDate: releaseDate, Added: added);

    private static WhisparrMoviePage MoviePage(params WhisparrMovie[] records)
        => new(Page: 1, PageSize: 50, TotalRecords: records.Length, Records: records);

    [Fact]
    public void Wanted_projects_a_monitored_fileless_scene_carrying_its_display_facts()
    {
        var row = Assert.Single(ActivityProjector.WantedPage(MoviePage(
            MovieRow(title: "Wanted Scene", studioTitle: "Vixen", releaseDate: "2026-02-03", added: "2026-06-09T12:00:00Z"))).Records);

        Assert.Equal("Wanted Scene", row.Scene.SceneTitle);
        Assert.Equal("Vixen", row.Scene.Studio);
        Assert.Equal("2026-02-03", row.Scene.Date);
        Assert.Equal("2026-06-09T12:00:00Z", row.AddedDate);
    }

    [Fact]
    public void Wanted_omits_a_scene_that_has_a_file_the_live_derivation_clears_on_import()
    {
        // A wanted scene that gains a file (on import) no longer matches monitored && !hasFile, so it is
        // simply absent — the read is a live derivation, never a persisted stale list.
        Assert.Empty(ActivityProjector.WantedPage(MoviePage(MovieRow(monitored: true, hasFile: true))).Records);
    }

    [Fact]
    public void Wanted_omits_an_unmonitored_scene()
        => Assert.Empty(ActivityProjector.WantedPage(MoviePage(MovieRow(monitored: false, hasFile: false))).Records);

    [Fact]
    public void Wanted_keeps_only_the_monitored_fileless_rows_of_a_mixed_page()
    {
        var result = ActivityProjector.WantedPage(MoviePage(
            MovieRow(id: 1, title: "keep", monitored: true, hasFile: false),
            MovieRow(id: 2, title: "hasFile", monitored: true, hasFile: true),
            MovieRow(id: 3, title: "unmonitored", monitored: false, hasFile: false)));

        var row = Assert.Single(result.Records);
        Assert.Equal("keep", row.Scene.SceneTitle);
    }

    [Fact]
    public void Wanted_projects_a_v2_episode_shaped_row_uniformly()
    {
        // A v2 wanted row binds its shared fields (title/monitored/hasFile/releaseDate/seriesId) into the movie
        // shape with no studioTitle/added — it still projects to the SAME WantedRow shape.
        var v2Row = MovieRow(title: "Episode Scene", studioTitle: null, added: null, seriesId: 7, itemType: null);
        var row = Assert.Single(ActivityProjector.WantedPage(MoviePage(v2Row)).Records);

        Assert.Equal("Episode Scene", row.Scene.SceneTitle);
        Assert.Null(row.Scene.Studio);
        Assert.Null(row.AddedDate);
    }

    [Fact]
    public void Wanted_titleless_row_falls_back_to_a_neutral_placeholder_never_null()
    {
        var row = Assert.Single(ActivityProjector.WantedPage(MoviePage(MovieRow(title: null))).Records);
        Assert.False(string.IsNullOrWhiteSpace(row.Scene.SceneTitle));
    }

    [Fact]
    public void A_null_wanted_records_envelope_projects_an_empty_result_not_an_error()
    {
        var result = ActivityProjector.WantedPage(new WhisparrMoviePage(Page: 1, PageSize: 50, TotalRecords: 0, Records: null));
        Assert.Empty(result.Records);
        Assert.Equal(1, result.Page);
    }
}
