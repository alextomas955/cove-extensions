using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Ingest;
using WhisparrSync.Library;
using WhisparrSync.Options;
using WhisparrSync.Safety;
using WhisparrSync.State;
using static WhisparrSync.Contracts.WireSerializers;

namespace WhisparrSync;

/// <summary>
/// The ingest slice's minimal-API surface: the anonymous inbound <c>/webhook</c> (token-authed, no
/// principal gate), the read-only import-activity log, and the re-grab-loop folder-overlap advisory. The
/// fail-closed root set the webhook ingest guard consults per event is read through
/// <see cref="WhisparrRootsPort"/>, which owns the cache.
/// </summary>
public sealed partial class WhisparrSync
{
    // The read-only import-activity log (review half): a pure read of the extension's own audit
    // journal, read-gated (extensions.read) exactly like /reconciliation.
    private const string ImportLogRoute = RouteBase + "/import-log";

    // The read-only folder-overlap advisory: root-vs-root containment on both generations, plus the Eros
    // scene-folder-format doubling where that concept exists. Configure-gated because it reaches the stored creds
    // to read Whisparr's roots and naming config, the same posture as /file-settings.
    private const string FolderOverlapRoute = RouteBase + "/folder-overlap";

    // The ONE anonymous route: inbound Whisparr On-Import events. Whisparr holds no Cove principal, so this is
    // the one route that declares the token tier — the shared secret validated inside WebhookReceiver is its
    // whole auth.
    private const string WebhookRoute = RouteBase + "/webhook";

    /// <summary>
    /// Registers the ingest slice's routes: the token-gated inbound <c>/webhook</c>, the read-tier import log,
    /// and the configure-tier folder-overlap advisory.
    /// </summary>
    private void MapIngestEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Bind the raw HttpContext so the receiver reads the token (X-Cove-Token header preferred; the query
        // fallback is documented) and the body itself. The coordinator resolves the scoped IScanService from the
        // captured factory and gates every ingest on the cached Whisparr root set (fail-closed when roots are
        // unavailable). WarnQueryTokenChannelOnce logs a one-time warning for the insecure query channel.
        endpoints.MapPost(WebhookRoute,
            (HttpContext http, WhisparrClient client, CancellationToken ct)
                => new WebhookReceiver(
                        Store,
                        new IngestCoordinator(ScopeFactory, c => ObservedRootsAsync(client, c)),
                        WarnQueryTokenChannelOnce,
                        reason => LogHealthRecordFailed(HealthDependency.Import, reason))
                    .HandleAsync(http, ct)).TokenGated();

        endpoints.MapGet(ImportLogRoute, (CancellationToken ct) => ImportLogAsync(ct)).ReadGated();

        // The advisory answers on both generations but reaches the stored creds to read the root folders (and,
        // on Eros, the naming config), which is what puts it at the configure tier rather than read.
        endpoints.MapGet(FolderOverlapRoute,
            (WhisparrClient client, CancellationToken ct)
                => FolderOverlapAsync(client, ct)).ConfigureGated();
    }

    /// <summary>
    /// Returns the pre-reduced auto-import status the settings UI consumes: <c>lastEventTicks</c> (the newest
    /// webhook delivery, for the "last event" line) and the <c>syncHealth</c> banner signal. A pure read of the
    /// extension's own bounded status record (reaches no credentials, opens no scope). Declares the read tier.
    /// </summary>
    /// <remarks>
    /// The full per-attempt journal (and its never-consumed <c>counts</c>) is gone — the store is a bounded
    /// status record and the endpoint returns only the two aggregates the UI reads.
    /// </remarks>
    internal async Task<IResult> ImportLogAsync(CancellationToken ct)
    {
        var status = await new ImportLog(Store).LoadStatusAsync(ct);
        var health = await new HealthStore(Store).LoadAsync(ct);
        return Results.Json(
            new ImportStatusResponse(
                LastEventTicks: status.LastWebhookEventTicks,
                SyncHealth: SyncHealthOf(status),
                PipelineHealth: PipelineHealthOf(health)),
            EnumStringResponseJsonOptions);
    }

    /// <summary>
    /// Projects the stored per-dependency health record for the settings readout, mapping a zero tick to null so
    /// a never-observed timestamp renders as an absence rather than as the epoch.
    /// </summary>
    /// <remarks>
    /// Only the entries actually present are projected: a dependency nothing has ever observed is OMITTED rather
    /// than emitted as healthy, so a degraded leg leaves part of the answer out instead of inventing it. It is a
    /// straight map and ages nothing itself: the handler reads through <c>HealthStore.LoadAsync</c>, which
    /// normalises against the current clock on every request, so a stale-looking response is answered at the
    /// store rather than by a second horizon here.
    /// </remarks>
    internal static IReadOnlyList<DependencyHealthView> PipelineHealthOf(DependencyHealth[] entries)
        => [.. entries.Select(e => new DependencyHealthView(
            Dependency: e.Dependency,
            Outcome: e.Outcome,
            LastHealthyTicks: e.LastHealthyTicks > 0 ? e.LastHealthyTicks : null,
            LastFailureTicks: e.LastFailureTicks > 0 ? e.LastFailureTicks : null,
            ConsecutiveFailures: e.ConsecutiveFailures,
            LastError: e.LastError))];

    /// <summary>
    /// The "sync is broken" signal for the settings banner, projected from the pre-reduced status record: the
    /// unresolved path-mismatch count (kept exact so the rendered count never truncates), its newest ticks, and
    /// up to three newest distinct sample paths from the bounded ring. A later success clears the window at
    /// write time, so the record only ever holds post-success failures.
    /// </summary>
    internal static SyncHealthView SyncHealthOf(ImportStatus status)
        => new(
            PathMismatch: status.UnresolvedPathMismatch,
            LastMismatchTicks: status.RecentFailures.Length > 0 ? status.RecentFailures.Max(f => f.UtcTicks) : null,
            SamplePaths: [.. status.RecentFailures
                .OrderByDescending(f => f.UtcTicks)
                .Select(f => f.Path)
                .Distinct(StringComparer.Ordinal)
                .Take(3)]);

    /// <summary>
    /// Answers whether the two systems' folders line up: every (Whisparr root, Cove root) pair where one contains
    /// the other — the shared root an import-in-place could echo back to Whisparr as a "new" grab — plus, on Eros,
    /// every Whisparr root whose trailing segment doubles the Scene Folder Format's leading literal. Returns facts
    /// only (the two finding shapes); every sentence is composed in the settings page.
    /// </summary>
    /// <remarks>
    /// Configure-gated: it reaches the stored credentials to call Whisparr, the same posture as
    /// <see cref="FileSettingsGetAsync"/>. Mutates nothing.
    /// <para>
    /// A read that could not run answers <c>checked:false</c> plus a <see cref="FolderOverlapReason"/> naming the
    /// real cause — never an empty finding array, which is indistinguishable from a genuine all-clear and was the
    /// defect this shape replaces. Every leg including every abstention is a 200: the client's read catch would
    /// swallow a 400 into that same false all-clear.
    /// </para>
    /// <para>
    /// The Scene Folder Format is an Eros concept and a v2 naming config carries none at all, so on v2 that KIND is
    /// listed in <c>notApplicable</c> and no naming read is issued — one wire call, not two. Root containment is
    /// answered on both generations: the root endpoint is byte-identical across them.
    /// </para>
    /// </remarks>
    internal async Task<IResult> FolderOverlapAsync(
        WhisparrClient client, CancellationToken ct)
    {
        var (options, baseUrl, apiKey) = await StoredCredsAsync(ct);
        var whisparrRoots = await RootsPort.ReadAsync(client, ct);
        if (whisparrRoots.Reason is { } unread)
        {
            return NotChecked(unread);
        }

        var coveRoots = await GetCoveRootsAsync(ct);
        if (coveRoots.Count == 0)
        {
            return NotChecked(FolderOverlapReason.CoveRootsUnknown);
        }

        var rootPaths = whisparrRoots.Paths;
        var findings = new List<object>();
        foreach (var overlap in RootOverlapDetector.Detect(rootPaths, coveRoots))
        {
            findings.Add(new RootContainmentFinding(
                FolderFindingKind.RootContainment, overlap.WhisparrRoot, overlap.CoveRoot));
        }

        var notApplicable = new List<string>();
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is V3Adapter)
        {
            var naming = await client.GetNamingConfigAsync(baseUrl, apiKey, ct);
            if (!naming.IsOk)
            {
                return NotChecked(FolderOverlapReason.ReadFailed);
            }

            foreach (var doubled in SceneFolderOverlapDetector.Detect(naming.Value!.SceneFolderFormat, rootPaths))
            {
                findings.Add(new SceneFolderFindingView(
                    FolderFindingKind.SceneFolderFormat, doubled.Root, doubled.Prefix, doubled.SuggestedRoot));
            }
        }
        else
        {
            notApplicable.Add(FolderFindingKind.SceneFolderFormat);
        }

        return Results.Json(
            new FolderOverlapResponse(Checked: true, Reason: null, Findings: findings, NotApplicable: notApplicable),
            EnumStringResponseJsonOptions);
    }

    // An abstention carries no findings at all: an empty array beside checked:false would invite a reader to treat
    // it as "nothing found", which is the conflation the reason exists to break.
    private static IResult NotChecked(string reason)
        => Results.Json(
            new FolderOverlapResponse(Checked: false, Reason: reason, Findings: [], NotApplicable: []),
            EnumStringResponseJsonOptions);

    /// <summary>
    /// Resolves the Cove library roots for the overlap check. Preferred source:
    /// <c>CoveConfiguration.CovePaths</c> resolved from a fresh scope when the host injects it. Fallback: the
    /// distinct parent folders of the library's own file paths via <see cref="CoveLibraryPort"/> — enough for
    /// an advisory containment comparison. Returns an empty set (no warning) when neither source is available;
    /// the overlap warning is advisory, so an unavailable source degrades to silence, never an error.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetCoveRootsAsync(CancellationToken ct)
    {
        if (_scopeFactory is null)
        {
            return [];
        }

        await using var scope = _scopeFactory.CreateAsyncScope();

        // Preferred: the host-configured media-library roots ScanService scans (VERIFIED to exist).
        if (scope.ServiceProvider.GetService<CoveConfiguration>() is { CovePaths: { Count: > 0 } covePaths })
        {
            return [.. covePaths
                .Select(p => p.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        // Fallback: derive distinct folders from the library's own files (needs no host-config access).
        if (scope.ServiceProvider.GetService<DbContext>() is not { } db)
        {
            return [];
        }

        var options = await new OptionsStore(Store).LoadAsync(ct);
        var port = new CoveLibraryPort(db, options.StashDbEndpoint, options.TpdbEndpoint);

        // Accumulated from the path stream rather than a materialized library: the answer is the distinct FOLDER
        // set, which is orders of magnitude smaller than the file set it is derived from.
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var filePath in port.StreamFilePathsAsync(ct))
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                continue;
            }

            var folder = Path.GetDirectoryName(filePath) ?? filePath;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                roots.Add(folder);
            }
        }

        return [.. roots];
    }

    /// <summary>
    /// The ambient root read the ingest guard consults per event, with the port's own classified outcome
    /// recorded on the way past. Returns exactly what the port returned.
    /// </summary>
    /// <remarks>
    /// This read is the only proof of Whisparr's reachability on a path with no user present, and until now its
    /// failure left no trace anywhere. The fail-closed contract is unchanged: a failed read still yields no
    /// roots, so the containment guard still rejects, and the port's cache and refill gate are untouched.
    /// <para>
    /// Bound to the WEBHOOK coordinator only, where one delivery is one ingest and therefore one record. The
    /// reconcile pass drives the same coordinator once per history ROW, so it keeps the untapped read and proves
    /// reachability through its own per-page tap instead.
    /// </para>
    /// </remarks>
    private async ValueTask<IReadOnlyList<string>> ObservedRootsAsync(WhisparrClient client, CancellationToken ct)
    {
        var result = await RootsPort.ReadAsync(client, ct);
        await HealthStore.TryRecordAsync(
            Store,
            HealthDependency.Acquisition,
            RootReadObservation(result),
            reason => LogHealthRecordFailed(HealthDependency.Acquisition, reason),
            ct);
        return result.Paths;
    }

    // A refusal that never reached the wire — no host stored, or a persisted version this build cannot manage —
    // is NOT-CHECKED rather than failed: nothing was asked of Whisparr, so recording a failure would be a claim
    // the code cannot support. Only a read that actually left and did not answer is a failure, and its state
    // comes from the port's own classification rather than a second reading of the same call.
    private static HealthObservation RootReadObservation(WhisparrRootsResult result) => result.Reason switch
    {
        null => HealthObservation.Healthy("ok"),
        FolderOverlapReason.ReadFailed => HealthOutcome.FromAcquisition(
            result.FailureState ?? WhisparrResultState.Unreachable, null),
        var reason => HealthObservation.NotChecked(reason),
    };

    // Five minutes is the cache lifetime the webhook guard has always had, and one port per extension instance
    // (rather than a static) keeps the cached root set bound to the loaded extension, not to the process.
    private WhisparrRootsPort? _rootsPort;

    private WhisparrRootsPort RootsPort => _rootsPort ??= new WhisparrRootsPort(
        StoredCredsAsync, TimeSpan.FromMinutes(5));
}
