using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisparrSync.Contracts;

/// <summary>
/// The kind of history event a scene row records — the three outcomes the activity surface renders. Pinned to
/// its camelCase wire name (<c>grabbed</c> / <c>imported</c> / <c>failed</c>) so the FE label/glyph map keys on
/// the exact strings and drift fails an offline gate, mirroring <see cref="SceneStatus.SceneWhisparrState"/>.
/// </summary>
/// <remarks>
/// WIRE CASING IS PINNED, by this type-level converter and by the camelCase naming policy
/// <c>EnumStringResponseJsonOptions</c> registers — the two agree, so both routes emit the same strings.
/// <c>WireEnumCasingTests</c> asserts the literal wire value of every member and is what fails on drift.
/// </remarks>
[JsonConverter(typeof(ActivityHistoryEventJsonConverter))]
internal enum ActivityHistoryEvent
{
    /// <summary>Whisparr grabbed a release for the scene (queued for download).</summary>
    Grabbed,

    /// <summary>Whisparr imported the downloaded file — the scene arrived.</summary>
    Imported,

    /// <summary>A grab or import failed.</summary>
    Failed,
}

/// <summary>Pins <see cref="ActivityHistoryEvent"/> to a camelCase string (<c>grabbed</c>/<c>imported</c>/<c>failed</c>) on the wire.</summary>
internal sealed class ActivityHistoryEventJsonConverter() : JsonStringEnumConverter<ActivityHistoryEvent>(JsonNamingPolicy.CamelCase);

/// <summary>
/// The uniform per-scene display row every activity section composes — Whisparr-owned display facts only,
/// every field nullable so an odd or partial upstream row still projects. It carries NO stored URL/API key
/// and no internal ids: only what the list renders.
/// </summary>
/// <param name="SceneTitle">The scene/release title, or a neutral placeholder when the upstream row carries none.</param>
/// <param name="Studio">The studio/site, or null when the row does not report it.</param>
/// <param name="Quality">The quality label, or null when absent.</param>
/// <param name="Date">The event date as an ISO-8601 string, or null when absent.</param>
internal sealed record ActivityScene(string SceneTitle, string? Studio, string? Quality, string? Date);

/// <summary>
/// One History row: a composed <see cref="ActivityScene"/> plus the event that produced it. The event carries a
/// property-level converter so its camelCase wire string is guaranteed even through options that register a
/// plain enum converter (see <see cref="ActivityHistoryEvent"/> remarks).
/// </summary>
internal sealed record HistoryRow(
    ActivityScene Scene,
    ActivityHistoryEvent Event);

/// <summary>
/// One page of the History projection — the paging facts echoed from Whisparr's own paged history envelope
/// (<see cref="Page"/>/<see cref="PageSize"/>/<see cref="TotalRecords"/>) plus the projected, filtered
/// <see cref="Records"/>. An outage never produces this shape (the endpoint returns a distinct error body);
/// an empty upstream page produces an empty <see cref="Records"/> list, never an error.
/// </summary>
internal sealed record HistoryPageResponse(int Page, int PageSize, int TotalRecords, IReadOnlyList<HistoryRow> Records);

/// <summary>
/// The normalized state of a Queue row — the five outcomes the Queue surface renders, collapsed from
/// Whisparr's own <c>status</c> / <c>trackedDownloadState</c> / <c>trackedDownloadStatus</c> triple (per the
/// rendered status/glyph vocabulary). Pinned camelCase for the same reason as
/// <see cref="ActivityHistoryEvent"/> (see its remarks): the FE label/glyph map keys on the exact wire strings.
/// </summary>
/// <remarks>
/// WIRE CASING IS PINNED the same way as <see cref="ActivityHistoryEvent"/>: the type-level converter and the
/// options' camelCase naming policy agree, and <c>WireEnumCasingTests</c> asserts each member's literal value.
/// </remarks>
[JsonConverter(typeof(ActivityQueueStateJsonConverter))]
internal enum ActivityQueueState
{
    /// <summary>The release is actively downloading.</summary>
    Downloading,

    /// <summary>Queued / delayed / paused — accepted by the download client but not yet transferring.</summary>
    Queued,

    /// <summary>The download finished and Whisparr is importing (or pending import of) the file.</summary>
    Importing,

    /// <summary>A non-fatal warning (a tracked-download warning or a delayed/unavailable client).</summary>
    Warning,

    /// <summary>A grab or import failed.</summary>
    Failed,
}

/// <summary>Pins <see cref="ActivityQueueState"/> to a camelCase string on the wire.</summary>
internal sealed class ActivityQueueStateJsonConverter() : JsonStringEnumConverter<ActivityQueueState>(JsonNamingPolicy.CamelCase);

/// <summary>
/// One Queue row: the composed <see cref="ActivityScene"/> display facts plus the normalized
/// <see cref="State"/>, a whole-percent <see cref="ProgressPercent"/> (0–100, or null when the upstream row
/// carries no determinate size/sizeleft), and a display <see cref="Eta"/> (Whisparr's own <c>timeleft</c>).
/// The state carries a property-level converter so its camelCase wire string survives the options that
/// register a plain enum converter (see <see cref="ActivityQueueState"/> remarks).
/// </summary>
internal sealed record QueueRow(
    ActivityScene Scene,
    ActivityQueueState State,
    int? ProgressPercent,
    string? Eta);

/// <summary>
/// One page of the Queue projection — the paging facts echoed from Whisparr's own paged queue envelope plus
/// the projected <see cref="Records"/>. An outage never produces this shape (the endpoint returns a distinct
/// error body); an empty upstream page produces an empty <see cref="Records"/> list, never an error.
/// </summary>
internal sealed record QueuePageResponse(int Page, int PageSize, int TotalRecords, IReadOnlyList<QueueRow> Records);

/// <summary>
/// One Wanted row: the composed <see cref="ActivityScene"/> display facts plus <see cref="AddedDate"/>
/// (Whisparr's own timestamp for when the scene entered the wanted set, or null when the row carries none).
/// </summary>
internal sealed record WantedRow(ActivityScene Scene, string? AddedDate);

/// <summary>
/// One page of the Wanted projection — the paging facts echoed from Whisparr's own paged wanted/missing
/// envelope plus the projected, filtered <see cref="Records"/>. An outage never produces this shape; an empty
/// upstream page produces an empty <see cref="Records"/> list, never an error.
/// </summary>
internal sealed record WantedPageResponse(int Page, int PageSize, int TotalRecords, IReadOnlyList<WantedRow> Records);
