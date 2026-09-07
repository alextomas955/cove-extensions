using WhisparrSync.Client;
using WhisparrSync.Contracts;

namespace WhisparrSync.Activity;

/// <summary>
/// Pure, I/O-free projection of Whisparr's own paged history envelope into the Cove-facing
/// <see cref="HistoryPageResponse"/>. It takes no client/DB/store handle, so "makes no outbound call" is
/// STRUCTURAL here, not mocked. Odd/partial upstream shapes degrade — an unrecognized event type is omitted, a
/// null/empty page projects to an empty records list (an empty result, never an error) — rather than throwing.
/// </summary>
internal static class ActivityProjector
{
    // Whisparr/Radarr history eventType strings the History surface recognizes. Anything else (a rename,
    // a file-delete, an ignored download) is not an acquisition outcome and is omitted from the projection.
    private const string ImportedEventType = "downloadFolderImported";
    private const string GrabbedEventType = "grabbed";
    private const string DownloadFailedEventType = "downloadFailed";
    private const string ImportFailedEventType = "importFailed";

    // Shown when an upstream row carries no title at all — an honest placeholder, never a fabricated title.
    // Shared with the acquired-but-not-landed derivation: one placeholder literal on the wire, not two.
    internal const string UntitledScene = "(untitled)";

    /// <summary>
    /// Projects one page of Whisparr history into the History response, echoing the paging facts and mapping
    /// each recognized record to a <see cref="HistoryRow"/> (unrecognized event types are dropped). A null
    /// <paramref name="raw"/> records array yields an empty list — an empty page is an empty result, not a fault.
    /// </summary>
    internal static HistoryPageResponse HistoryPage(WhisparrHistoryPage raw)
    {
        var records = new List<HistoryRow>();
        foreach (var record in raw.Records ?? [])
        {
            if (MapEvent(record.EventType) is not { } evt)
            {
                continue;
            }

            records.Add(new HistoryRow(SceneOf(record), evt));
        }

        return new HistoryPageResponse(raw.Page, raw.PageSize, raw.TotalRecords, records);
    }

    // Maps a Whisparr eventType to the projected event, or null when it is not an acquisition outcome the
    // History surface renders (so the caller omits the row rather than inventing a state).
    private static ActivityHistoryEvent? MapEvent(string? eventType)
    {
        if (string.Equals(eventType, ImportedEventType, StringComparison.OrdinalIgnoreCase))
        {
            return ActivityHistoryEvent.Imported;
        }

        if (string.Equals(eventType, GrabbedEventType, StringComparison.OrdinalIgnoreCase))
        {
            return ActivityHistoryEvent.Grabbed;
        }

        if (string.Equals(eventType, DownloadFailedEventType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(eventType, ImportFailedEventType, StringComparison.OrdinalIgnoreCase))
        {
            return ActivityHistoryEvent.Failed;
        }

        return null;
    }

    // A history record carries no studio or quality, so both bind null; the title falls back to a neutral
    // placeholder rather than a fabricated one.
    private static ActivityScene SceneOf(WhisparrHistoryRecord record)
    {
        var title = string.IsNullOrWhiteSpace(record.SourceTitle) ? UntitledScene : record.SourceTitle!;
        return new ActivityScene(title, Studio: null, Quality: null, Date: record.Date);
    }

    // Whisparr's tracked-download status/state vocabulary (Radarr/Sonarr shared). Only the strings the queue
    // normalization branches on are named; anything else falls through to the Downloading default.
    private const string StatusFailed = "failed";
    private const string StatusWarning = "warning";
    private const string StatusPaused = "paused";
    private const string StatusQueued = "queued";
    private const string StatusDelay = "delay";
    private const string TrackedStatusError = "error";
    private const string TrackedStatusWarning = "warning";
    private const string TrackedStateImporting = "importing";
    private const string TrackedStateImportPending = "importPending";

    /// <summary>
    /// Projects one page of the Whisparr queue into the Queue response, echoing the paging facts and normalizing
    /// each row to a <see cref="QueueRow"/> (the version-invariant state + progress + ETA + display facts). A null
    /// <paramref name="raw"/> records array yields an empty list — an empty queue is an empty result, not a fault.
    /// </summary>
    internal static QueuePageResponse QueuePage(WhisparrQueuePage raw)
    {
        var records = new List<QueueRow>();
        foreach (var record in raw.Records ?? [])
        {
            records.Add(new QueueRow(
                QueueSceneOf(record),
                QueueStateOf(record),
                ProgressPercentOf(record.Size, record.Sizeleft),
                Eta: string.IsNullOrWhiteSpace(record.Timeleft) ? null : record.Timeleft));
        }

        return new QueuePageResponse(raw.Page, raw.PageSize, raw.TotalRecords, records);
    }

    // Studio is the v3 movie's studioTitle OR the v2 site title, whichever the version's row carries — one
    // shape covers both. Title/quality fall back defensively so a partial row still renders.
    private static ActivityScene QueueSceneOf(WhisparrQueueRecord record)
    {
        var title = FirstNonBlank(record.Title, record.Movie?.Title, record.Series?.Title) ?? UntitledScene;
        var studio = FirstNonBlank(record.Movie?.StudioTitle, record.Series?.Title);
        var quality = record.Quality?.Quality?.Name;
        return new ActivityScene(title, studio, string.IsNullOrWhiteSpace(quality) ? null : quality, record.EstimatedCompletionTime);
    }

    // Collapse Whisparr's status / trackedDownloadState / trackedDownloadStatus triple into the five-state UI
    // vocabulary. Precedence (highest first): a hard failure, then a warning, then importing, then queued, else
    // downloading — so a warning-while-importing still reads as the more actionable Warning, and only a clean
    // in-flight transfer reads as Downloading.
    private static ActivityQueueState QueueStateOf(WhisparrQueueRecord record)
    {
        if (Eq(record.TrackedDownloadStatus, TrackedStatusError)
            || Eq(record.Status, StatusFailed)
            || Contains(record.TrackedDownloadState, StatusFailed))
        {
            return ActivityQueueState.Failed;
        }

        if (Eq(record.TrackedDownloadStatus, TrackedStatusWarning) || Eq(record.Status, StatusWarning))
        {
            return ActivityQueueState.Warning;
        }

        if (Eq(record.TrackedDownloadState, TrackedStateImporting) || Eq(record.TrackedDownloadState, TrackedStateImportPending))
        {
            return ActivityQueueState.Importing;
        }

        if (Eq(record.Status, StatusQueued) || Eq(record.Status, StatusDelay) || Eq(record.Status, StatusPaused))
        {
            return ActivityQueueState.Queued;
        }

        return ActivityQueueState.Downloading;
    }

    // A clamped whole-percent from size/sizeleft; null (indeterminate) when either is absent or size is
    // non-positive — a percent can't be computed without a positive total.
    private static int? ProgressPercentOf(double? size, double? sizeleft)
    {
        if (size is not { } total || sizeleft is not { } left || total <= 0)
        {
            return null;
        }

        var fraction = (total - left) / total * 100;
        return Math.Clamp((int)Math.Round(fraction), 0, 100);
    }

    /// <summary>
    /// Projects one page of Whisparr's wanted/missing set into the Wanted response. INCLUDES only monitored
    /// scenes without a file — defense-in-depth even though the query already filters — so the read is a pure
    /// live derivation: a scene that gains a file (on import) no longer matches and is simply absent. A
    /// null <paramref name="raw"/> records array yields an empty list.
    /// </summary>
    internal static WantedPageResponse WantedPage(WhisparrMoviePage raw)
    {
        var records = new List<WantedRow>();
        foreach (var record in raw.Records ?? [])
        {
            if (!record.Monitored || record.HasFile)
            {
                continue;
            }

            var title = string.IsNullOrWhiteSpace(record.Title) ? UntitledScene : record.Title!;
            var studio = string.IsNullOrWhiteSpace(record.StudioTitle) ? null : record.StudioTitle;
            records.Add(new WantedRow(
                new ActivityScene(title, studio, Quality: null, Date: record.ReleaseDate),
                AddedDate: record.Added));
        }

        return new WantedPageResponse(raw.Page, raw.PageSize, raw.TotalRecords, records);
    }

    private static bool Eq(string? value, string expected)
        => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string? value, string fragment)
        => value is not null && value.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonBlank(params string?[] candidates)
        => Array.Find(candidates, c => !string.IsNullOrWhiteSpace(c));
}
