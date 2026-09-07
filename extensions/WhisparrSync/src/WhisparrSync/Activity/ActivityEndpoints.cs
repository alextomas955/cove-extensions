using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WhisparrSync.Activity;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using static WhisparrSync.Contracts.WireSerializers;

namespace WhisparrSync;

/// <summary>
/// The read-only activity slice: recently grabbed/imported/failed scenes projected from Whisparr's own paged
/// history. Read-only (GET only), configure-gated + stored-creds-only, and it grabs nothing — the projection
/// reads through <see cref="ActivityProjector"/> from the fetched history page.
/// </summary>
public sealed partial class WhisparrSync
{
    // Recently grabbed/imported/failed scenes — a bodiless GET with optional page/pageSize query. Configure-
    // gated + stored-creds-only: no body carries a url/key; the stored key is never echoed.
    private const string ActivityHistoryRoute = RouteBase + "/activity/history";

    // The live Whisparr download queue — same gate/creds/shape as history. Bodiless GET, optional page/pageSize.
    private const string ActivityQueueRoute = RouteBase + "/activity/queue";

    // The live wanted set (monitored && no file), derived live from Whisparr — same gate/creds/shape as history.
    private const string ActivityWantedRoute = RouteBase + "/activity/wanted";

    // Server-paged read defaults; a caller-supplied pageSize is clamped so no request can pull an unbounded page.
    private const int DefaultActivityPageSize = 50;
    private const int MaxActivityPageSize = 100;

    /// <summary>
    /// Registers the activity slice's routes. Every grouping is a bodiless GET at the configure tier — each one
    /// reads Whisparr with the stored key.
    /// </summary>
    private void MapActivityEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(ActivityHistoryRoute,
            (int? page, int? pageSize, WhisparrClient client, CancellationToken ct)
                => ActivityHistoryAsync(page, pageSize, client, ct)).ConfigureGated();

        endpoints.MapGet(ActivityQueueRoute,
            (int? page, int? pageSize, WhisparrClient client, CancellationToken ct)
                => ActivityQueueAsync(page, pageSize, client, ct)).ConfigureGated();

        endpoints.MapGet(ActivityWantedRoute,
            (int? page, int? pageSize, WhisparrClient client, CancellationToken ct)
                => ActivityWantedAsync(page, pageSize, client, ct)).ConfigureGated();
    }

    /// <summary>
    /// The resolved front half of an activity read, or the response that ends it before Whisparr is touched.
    /// </summary>
    /// <remarks>
    /// <see cref="CanRead"/> is the discriminator: while it is <c>false</c> the only meaningful member is
    /// <see cref="Denied"/>.
    /// </remarks>
    private sealed record ActivityRead(
        IResult? Denied,
        string? BaseUrl,
        string? ApiKey,
        IWhisparrAdapter? Adapter,
        int Page,
        int PageSize)
    {
        /// <summary>
        /// Whether the read may proceed against Whisparr: <c>false</c> guarantees <see cref="Denied"/> holds the
        /// response to return, <c>true</c> guarantees every resolved member is present.
        /// </summary>
        [MemberNotNullWhen(false, nameof(Denied))]
        [MemberNotNullWhen(true, nameof(BaseUrl), nameof(ApiKey), nameof(Adapter))]
        internal bool CanRead => Adapter is not null;
    }

    /// <summary>
    /// The front half every activity read shares: the stored-creds resolve, the generation selection, and the
    /// page clamp.
    /// </summary>
    /// <remarks>
    /// Each handler keeps its own upstream call, its 502 discriminator and its log line — those name the
    /// section, which is the whole value of the log line.
    /// </remarks>
    private async Task<ActivityRead> ResolveActivityReadAsync(
        int? page, int? pageSize, WhisparrClient client, CancellationToken ct)
    {
        // Stored creds only: the blank request resolves the stored host+key. A caller-supplied host can never be
        // paired with the stored key, and the key is never echoed.
        var (options, baseUrl, apiKey) = await StoredCredsAsync(ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            var unsupported = VersionUnsupported();
            return new ActivityRead(unsupported, null, null, null, 0, 0);
        }

        var resolvedPage = page is > 0 ? page.Value : 1;
        var resolvedPageSize = pageSize is > 0 and <= MaxActivityPageSize ? pageSize.Value : DefaultActivityPageSize;
        return new ActivityRead(null, baseUrl, apiKey, adapter, resolvedPage, resolvedPageSize);
    }

    /// <summary>
    /// Projects one page of Whisparr history (recently grabbed/imported/failed scenes) for the activity
    /// sub-page. Reads only — no Cove or Whisparr mutation, no grab. A non-Ok upstream read returns a distinct
    /// error discriminator (502) so the UI branches error vs empty: an outage is NEVER rendered as an empty
    /// list.
    /// </summary>
    internal async Task<IResult> ActivityHistoryAsync(
        int? page, int? pageSize, WhisparrClient client, CancellationToken ct)
    {
        var read = await ResolveActivityReadAsync(page, pageSize, client, ct);
        if (!read.CanRead)
        {
            return read.Denied;
        }

        var history = await read.Adapter.ListHistoryAsync(read.BaseUrl, read.ApiKey, read.Page, read.PageSize, ct);
        if (!history.IsOk)
        {
            // Best-effort read failure: one terse log line (a safe discriminator, never the key/host) and a
            // distinct error body so the UI shows the outage banner, never a falsely-empty History list.
            var discriminator = FailureDiscriminator(history.State);
            LogActivityReadFailed("history", discriminator);
            return Results.Json(new ActivityOutageResponse(discriminator), statusCode: 502);
        }

        var projection = ActivityProjector.HistoryPage(history.Value!);
        LogActivityHistoryRead(projection.Records.Count);
        return Results.Json(projection, EnumStringResponseJsonOptions);
    }

    /// <summary>
    /// Projects the live Whisparr download queue for the activity sub-page. Reads only — no Cove or Whisparr
    /// mutation, no grab. Same gate/creds/error shape as <see cref="ActivityHistoryAsync"/>: a non-Ok upstream
    /// read returns a distinct error discriminator (502) so an outage is never rendered as an empty queue.
    /// </summary>
    internal async Task<IResult> ActivityQueueAsync(
        int? page, int? pageSize, WhisparrClient client, CancellationToken ct)
    {
        var read = await ResolveActivityReadAsync(page, pageSize, client, ct);
        if (!read.CanRead)
        {
            return read.Denied;
        }

        var queue = await read.Adapter.ListQueueAsync(read.BaseUrl, read.ApiKey, read.Page, read.PageSize, ct);
        if (!queue.IsOk)
        {
            var discriminator = FailureDiscriminator(queue.State);
            LogActivityReadFailed("queue", discriminator);
            return Results.Json(new ActivityOutageResponse(discriminator), statusCode: 502);
        }

        var projection = ActivityProjector.QueuePage(queue.Value!);
        LogActivityQueueRead(projection.Records.Count);
        return Results.Json(projection, EnumStringResponseJsonOptions);
    }

    /// <summary>
    /// Projects the live wanted set (monitored scenes without a file) for the activity sub-page. Wanted is a LIVE
    /// derivation, never a persisted list, so an imported scene simply drops out. Reads only — no
    /// mutation, no grab. Same gate/creds/error shape as <see cref="ActivityHistoryAsync"/>.
    /// </summary>
    internal async Task<IResult> ActivityWantedAsync(
        int? page, int? pageSize, WhisparrClient client, CancellationToken ct)
    {
        var read = await ResolveActivityReadAsync(page, pageSize, client, ct);
        if (!read.CanRead)
        {
            return read.Denied;
        }

        var wanted = await read.Adapter.ListWantedAsync(read.BaseUrl, read.ApiKey, read.Page, read.PageSize, ct);
        if (!wanted.IsOk)
        {
            var discriminator = FailureDiscriminator(wanted.State);
            LogActivityReadFailed("wanted", discriminator);
            return Results.Json(new ActivityOutageResponse(discriminator), statusCode: 502);
        }

        var projection = ActivityProjector.WantedPage(wanted.Value!);
        LogActivityWantedRead(projection.Records.Count);
        return Results.Json(projection, EnumStringResponseJsonOptions);
    }
}
