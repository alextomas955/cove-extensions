using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Api;
using Renamer.Engine;
using Renamer.Execution;
using Renamer.Jobs;
using Renamer.Options;
using Renamer.Planner;
using static Cove.Extensions.Shared.MinimalApiPermissions;

namespace Renamer;

/// <summary>
/// The Cove-facing surface of the extension: the "Rename selected" bulk action
/// (contributed through <see cref="GetUIManifest"/> — <see cref="FullExtensionBase"/> does NOT
/// implement <c>IActionExtension</c>, so there is no <c>GetActions()</c> to override), the
/// <c>renamer-batch</c> job registration, and the <c>/preview</c> + <c>/renamer</c> minimal-API
/// endpoints. This partial stays disjoint from <c>Renamer.cs</c> (the shared batch core) and the
/// auto-renamer event partial.
/// </summary>
public sealed partial class Renamer
{
    // The action's endpoint reference and the mapped route MUST be the same literal, so derive
    // both from one base. The route prefix mirrors how the host mounts an extension's
    // IApiExtension endpoints: /api/extensions/{id}/…
    private const string RouteBase = "/api/extensions/com.alextomas955.renamer";
    private const string RenamerRoute = RouteBase + "/renamer";
    private const string PreviewRoute = RouteBase + "/preview";
    private const string PreviewSampleRoute = RouteBase + "/preview-sample";
    private const string UndoRoute = RouteBase + "/undo";
    private const string LastBatchRoute = RouteBase + "/last-batch";
    private const string ListStudiosRoute = RouteBase + "/list-studios";
    private const string ListTagsRoute = RouteBase + "/list-tags";
    private const string ListPerformersRoute = RouteBase + "/list-performers";
    private const string ScanLibraryRoute = RouteBase + "/scan-library";
    private const string LastScanRoute = RouteBase + "/last-scan";
    private const string RenamerLibraryRoute = RouteBase + "/renamer-library";

    /// <summary>The fixed <see cref="IExtensionStore"/> key the whole-library scan's persisted result lives under.</summary>
    private const string LastScanResultKey = "last-scan-result";

    // Upper bound on how many ids a single preview/renamer request may carry. Preview runs the planner
    // (DB hits) per id synchronously on the request thread, and renamer fans the same ids out into one
    // job — so a caller-supplied array is an unbounded fan-out. The cap rejects a runaway/oversized
    // request up front with a 400, before any per-id work, while staying far above any realistic
    // selection. A genuinely larger job should be split into batches by the caller.
    private const int MaxEntityIdsPerRequest = 1000;

    /// <summary>
    /// Contributes the "Rename selected" bulk action — registered ONCE PER ENTITY KIND (video, image)
    /// so each carries the matching <c>RequiredPermission</c> (<c>videos.write</c> / <c>images.write</c>).
    /// The host's action model allows only a single <c>RequiredPermission</c> per action and filters an
    /// action's visibility by both the current entity-type context AND that permission, so a single
    /// video+image action gated on <c>videos.write</c> would hide the button from an images-only-write
    /// user viewing images. Splitting per kind gives each entity context the correct visibility gate.
    /// Audio is reachable via the job/API directly but not surfaced as a bulk button. Each action
    /// declares <c>HandlerName="renamerSelected"</c> and NO <c>ApiEndpoint</c>: the host dispatches the JS
    /// handler the bundle registers instead of POSTing directly, so the handler can
    /// preview → <c>window.confirm</c> → POST <see cref="RenamerRoute"/>, returning <c>{cancelled:true}</c>
    /// on Cancel (the confirm-before-disk gate). <c>RequiredPermission</c> is a UI affordance ONLY; the
    /// <c>/renamer</c> and <c>/undo</c> endpoints re-check the request kind's permission server-side.
    /// </summary>
    public override UIManifest GetUIManifest()
        => ManifestBuilder()
            .AddAction(
                id: "renamer-selected-video",
                label: "Rename selected",
                actionType: "bulk",
                entityTypes: ["video"],
                icon: "pencil",
                apiEndpoint: null,
                handlerName: "renamerSelected",
                order: 100,
                requiredPermission: Permissions.VideosWrite,
                // The rename runs as a job (showInTaskList) that reports into the top-right Job Drawer, so the
                // host's queued-success window.alert is suppressed. The before-disk window.confirm gate stays.
                suppressSuccessAlert: true)
            .AddAction(
                id: "renamer-selected-image",
                label: "Rename selected",
                actionType: "bulk",
                entityTypes: ["image"],
                icon: "pencil",
                apiEndpoint: null,
                handlerName: "renamerSelected",
                order: 100,
                requiredPermission: Permissions.ImagesWrite,
                suppressSuccessAlert: true)
            // The renamer UI's home is a DEDICATED SETTINGS PAGE under the Settings → Extensions group.
            // Renamer is an app-like configurator (template editor, live preview, whole-library run,
            // undo) that doesn't fit a stack of uniform section cards, so the tab uses page layout:
            // the host renders the tab's panel full-width with no card chrome, and this extension owns
            // the whole canvas (see Cove's SettingsTabLayout.Page). A page sources its content from the
            // panels targeting it exactly like the default layout — only the chrome differs — so the
            // "RenamerPage" component is contributed as a section, whose componentName MUST equal the
            // key in the bundle's defineExtension components map. The bulk action above is unaffected.
            // (Requires the host's page-layout settings support — see minCoveVersion.)
            .AddSettingsTab(
                key: "renamer",
                label: "Renamer",
                description: "Build a filename from each item's metadata. Preview before it touches disk.",
                order: 100,
                layout: SettingsTabLayout.Page)
            .AddSettingsSection(targetTab: "renamer", label: "Renamer", componentName: "RenamerPage")
            .WithJsBundle("index.mjs")
            .Build();

    /// <summary>
    /// Registers the batch-renamer job. Its runner is the shared <see cref="RunRenamerBatchAsync"/>,
    /// which consumes the extension-flavored <c>IJobProgress</c> the host hands the runner
    /// directly — no adapter on this path. Invoked from the <see cref="FullExtensionBase"/> ctor, so
    /// <see cref="RenamerJob.JobId"/> must already exist when this runs.
    /// </summary>
    protected override void DefineJobs()
        => Job(
            id: RenamerJob.JobId,
            name: "Rename selected",
            handler: (parameters, progress, ct) => RunRenamerBatchAsync(parameters, progress, ct),
            description: "Batch-renames the selected media items from the configured template.",
            supportsParameters: true,
            showInTaskList: true);

    /// <summary>
    /// Maps the POST endpoints. Each lambda IMMEDIATELY delegates to an extracted instance
    /// method so the logic is unit-testable without an HTTP host — <c>WebApplicationFactory</c> can't
    /// mount extension routes, so we test the extracted methods directly. The host resolves the lambda
    /// parameters from the request scope; <c>ICurrentPrincipalAccessor</c> is populated by the host's
    /// CurrentPrincipalMiddleware.
    /// </summary>
    public override void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(PreviewRoute,
            (RenamerRequest req, DbContext db, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => PreviewAsync(req, db, principal, ct));

        endpoints.MapPost(RenamerRoute,
            (RenamerRequest req, ICurrentPrincipalAccessor principal, IJobService jobs)
                => RenamerEnqueue(req, principal, jobs));

        // NB: this endpoint binds the RAW HttpContext (not a typed PreviewSampleRequest) so the
        // handler can deserialize the body with RenamerOptions.JsonOptions — the host's default
        // minimal-API JsonSerializerOptions has NO JsonStringEnumConverter, so a body carrying
        // string enum values (e.g. "case":"Lower") would 400 on typed binding before the handler
        // ran. Extension code cannot touch host startup (ConfigureHttpJsonOptions), so we parse
        // the body ourselves with the converter-aware options.
        endpoints.MapPost(PreviewSampleRoute,
            (HttpContext http, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => PreviewSampleAsync(http.Request, principal, ct));

        // /undo takes NO request body — it operates on "the last batch", so binding no body avoids
        // the host's enum-converter 400 trap (see the preview-sample note above); /last-batch is a plain read.
        endpoints.MapPost(UndoRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => UndoAsync(principal, ct));

        endpoints.MapGet(LastBatchRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => LastBatchAsync(principal, ct));

        endpoints.MapGet(ListStudiosRoute,
            (DbContext db, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => ListStudiosAsync(db, principal, ct));

        endpoints.MapGet(ListTagsRoute,
            (DbContext db, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => ListTagsAsync(db, principal, ct));

        endpoints.MapGet(ListPerformersRoute,
            (DbContext db, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => ListPerformersAsync(db, principal, ct));

        endpoints.MapPost(ScanLibraryRoute,
            (ScanLibraryRequest? body, ICurrentPrincipalAccessor principal, IJobService jobs) =>
                ScanLibraryEnqueue(body, principal, jobs));

        endpoints.MapGet(LastScanRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => ScanLibraryResultAsync(principal, ct));

        endpoints.MapPost(RenamerLibraryRoute,
            (ICurrentPrincipalAccessor principal, IJobService jobs) => RenamerLibraryEnqueue(principal, jobs));
    }

    /// <summary>
    /// The synchronous, read-only dry-run: runs the planner over each requested
    /// id and returns the accumulated <see cref="RenamerPlanItem"/>[] (old→new + status) — ZERO
    /// mutation. Enforces <c>videos.read</c> in-handler because the host's <c>[RequiresPermission]</c>
    /// filter is MVC-only and inert on minimal-API endpoints.
    /// </summary>
    internal async Task<IResult> PreviewAsync(
        RenamerRequest req, DbContext db, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        // Resolve the kind FIRST so the permission check below gates on the request's own entity kind
        // (videos/images/audios.read) rather than always videos.read. An unparseable kind is a 400
        // before the auth check leaks nothing — it carries no ids and reads no data either way.
        if (!TryParseKind(req.EntityType, out var kind))
        {
            return Results.BadRequest(new { code = "UNSUPPORTED_ENTITY_TYPE" });
        }

        var (readPermission, _) = PermissionsFor(kind);
        if (Forbidden(principal, readPermission) is { } denied)
        {
            return denied;
        }

        // Reject an oversized id array before any per-id DB work (see MaxEntityIdsPerRequest).
        if (req.EntityIds.Length > MaxEntityIdsPerRequest)
        {
            return Results.BadRequest(new { code = "TOO_MANY_IDS", max = MaxEntityIdsPerRequest });
        }

        var options = await new OptionsStore(Store).LoadAsync(ct);
        var port = new CoveRenamerDataPort(db);
        var planner = new RenamerPlanner(port);

        // Build the SAME RouteLookups the batch builds and route through the routing overload,
        // so the dry-run reflects the routed destination the batch will execute (not the empty-lookups
        // source-confine fallback). Preview must match execution — the core value.
        var lookups = BuildLookups(options);

        var items = new List<RenamerPlanItem>();
        var sizeByFileId = new Dictionary<int, long>();
        foreach (var id in req.EntityIds)
        {
            ct.ThrowIfCancellationRequested();
            var plan = await planner.PlanAsync(kind, id, options, lookups, ct);
            items.AddRange(plan.Items);

            // File sizes for the blast-radius byte sums live on the loaded entity's files, not on the
            // plan item. Load the entity once (AsNoTracking — still zero mutation) and record each
            // file's bytes by id; the aggregate reads them per acting item. Mirrors the batch's PHASE A.
            var entity = await port.LoadEntityAsync(kind, id, ct);
            if (entity is not null)
            {
                foreach (var file in entity.Files)
                {
                    sizeByFileId[file.FileId] = file.SizeBytes;
                }
            }
        }

        // The whole-batch blast radius: a pure aggregate over the acting items + their sizes.
        var summary = BatchPreview.Summarize(items, sizeByFileId);

        // Serialize explicitly with PreviewResponseJsonOptions so the wire shape matches what the UI
        // bundle reads: camelCase property names AND the RenamerStatus/ConfirmLevel enums as STRINGS
        // ("Renamer"/"NoOp"/"SkipGated"…, "Light"/"Standard"/"Heavy"). The host's default minimal-API
        // serializer is camelCase but emits NUMERIC enums (status:0) — the frontend's
        // buildConfirmSummary matches on it.status === "Renamer", so a numeric enum reads as a
        // non-renamer and the renamer would silently never fire. Extension code cannot touch host
        // startup (ConfigureHttpJsonOptions), so we serialize here. (RenamerOptions.JsonOptions is
        // PascalCase + tolerant-read for the options round-trip — wrong casing for a response — hence
        // this dedicated instance.) The response is { items, summary }; the per-item array keeps its
        // exact camelCase string-enum shape because both halves ride this SAME options instance.
        return Results.Json(new PreviewResponse(items, summary), PreviewResponseJsonOptions);
    }

    /// <summary>
    /// Response-serialization options for <see cref="PreviewRoute"/>: camelCase to match the host's
    /// wire convention (and the UI's <c>PreviewItem</c> field names) plus a
    /// <see cref="JsonStringEnumConverter"/> so <c>status</c> serializes as the string the UI matches.
    /// </summary>
    private static readonly JsonSerializerOptions PreviewResponseJsonOptions = CoveJsonOptions.WebWithEnumStrings();

    /// <summary>
    /// Enqueues the batch-renamer job: encodes the request into the job params and hands the host
    /// a delegate that adapts the core <see cref="Cove.Core.Interfaces.IJobProgress"/> via <c>HostProgress</c> and
    /// calls the shared <see cref="RunRenamerBatchAsync"/>. Returns 202 {jobId}. Re-checks
    /// <c>videos.write</c> in-handler (the host permission filter is inert on minimal-API endpoints)
    /// — and crucially returns 403 BEFORE any enqueue.
    /// </summary>
    internal IResult RenamerEnqueue(RenamerRequest req, ICurrentPrincipalAccessor principal, IJobService jobs)
    {
        // Kind first so the write check gates on the request's own kind (videos/images/audios.write).
        if (!TryParseKind(req.EntityType, out var kind))
        {
            return Results.BadRequest(new { code = "UNSUPPORTED_ENTITY_TYPE" });
        }

        var (_, writePermission) = PermissionsFor(kind);
        if (Forbidden(principal, writePermission) is { } denied)
        {
            return denied;
        }

        // Reject an oversized id array before encoding/enqueuing the job (see MaxEntityIdsPerRequest).
        if (req.EntityIds.Length > MaxEntityIdsPerRequest)
        {
            return Results.BadRequest(new { code = "TOO_MANY_IDS", max = MaxEntityIdsPerRequest });
        }

        var parameters = RenamerJob.Encode(req.EntityType, req.EntityIds);

        // Enqueue EXCLUSIVE (the host's JobService default): a renamer batch mutates disk + DB, so two
        // batches running at once could plan against each other's stale snapshots or target the same
        // paths. Exclusive serializes them — the second waits for the first to finish.
        var jobId = jobs.Enqueue(
            $"ext:{Id}:{RenamerJob.JobId}",
            $"[{Name}] Rename selected",
            (coreProgress, ct) => RunRenamerBatchAsync(parameters, new HostProgress(coreProgress), ct),
            exclusive: true);

        return Results.Accepted(value: new { jobId });
    }

    /// <summary>
    /// Reverse-replays the most recent renamer batch. Enforces <c>videos.write</c>
    /// in-handler and returns 403 BEFORE any RevertLog read / scope open / disk touch (the
    /// host's <c>[RequiresPermission]</c> filter is inert on minimal-API endpoints; mirrors
    /// <see cref="RenamerEnqueue"/>). Takes no body — it always targets "the last open batch".
    /// <para>
    /// Reads the last still-open batch (its <see cref="RenamerFileKind"/> from the <c>#batch</c>
    /// header, its entityId-bearing rows newest-first), reverse-replays it via
    /// <see cref="UndoReplayer"/> (kind from the header, entityId from each row — there is NO
    /// hardcoded Video default on this path), then marks the batch consumed so a SECOND <c>/undo</c>
    /// finds no open batch and returns <c>{undone:0}</c>. The batch is marked consumed even
    /// on a partial failure: undo is not retried per-entry;
    /// the failed/skipped buckets report what was left behind. A null/empty batch (no batch, empty
    /// log, or already-consumed) is a clean <c>{undone:0}</c> no-op.
    /// </para>
    /// </summary>
    internal async Task<IResult> UndoAsync(ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        // 403 FIRST for a caller holding NO renamer-write permission of any kind — before any RevertLog
        // read or disk touch, so an unauthorized caller cannot even learn whether a batch exists. The
        // SPECIFIC kind's write permission is re-checked below once the batch header reveals the kind;
        // this coarse gate only preserves the "no read/disk work for the wholly-unauthorized" property.
        bool canWriteAny = principal.Current is not null
            && (principal.Current.Has(Permissions.VideosWrite)
                || principal.Current.Has(Permissions.ImagesWrite)
                || principal.Current.Has(Permissions.AudiosWrite));
        if (!canWriteAny)
        {
            return Results.Json(new { code = "FORBIDDEN" }, statusCode: 403);
        }

        var revertLog = new RevertLog(Store);

        // The summary carries the runId we mark consumed after the replay.
        var summary = await revertLog.ReadLastBatchSummaryAsync(ct);
        var batch = await revertLog.ReadLastOpenBatchAsync(ct);
        // Guard `summary` explicitly rather than relying on the implicit (and non-atomic)
        // coupling that `batch != null` forces `summary != null`. The two reads are separate store
        // reads; treating a missing summary as a clean no-op makes the nullability safe instead of
        // `!`-suppressed, so a future parser change (e.g. a header-only batch returning a null summary)
        // can never turn the `summary.Value.RunId` dereferences below into a runtime NRE.
        if (batch is null || batch.Entries.Count == 0 || summary is null)
        {
            return Results.Ok(new UndoResult(0, [], []));
        }

        // Re-gate on the WRITE permission of the kind that was actually renamed (the batch header
        // carries it) — undoing an image renamer requires images.write, not videos.write. This is
        // checked after the batch read (needed to learn the kind) but BEFORE the options load, scope
        // open, or any disk touch, so an under-permissioned caller still mutates nothing.
        var (_, undoWritePermission) = PermissionsFor(batch.Kind);
        if (Forbidden(principal, undoWritePermission) is { } denied)
        {
            return denied;
        }

        // Load the configured options AFTER the 403-first check and the empty-batch early-return (so an
        // unauthorized or no-op request still short-circuits without the load), mirroring PreviewAsync.
        // The undo RE-GATES each restore target against options.AllowedRoots — the same write boundary
        // the forward move used — so a restore can never land outside the allowed roots.
        var options = await new OptionsStore(Store).LoadAsync(ct);

        // Open a scope the SAME way RunRenamerBatchAsync does and resolve the scoped DbContext.
        await using var scope = ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();

        var replayer = new UndoReplayer(new CoveRenamerDataPort(db), EventBus, new DiskMover(),
            cross: new CrossVolumeMover(), allowedRoots: options.AllowedRoots);
        var run = await replayer.RevertAsync(batch, ct);

        LogUndo(summary.Value.RunId, batch, run);

        // Consume the batch only when at least one entry was actually restored. A run that restored
        // nothing — every entry skipped or failed — must leave the batch OPEN so the operation can be
        // retried after the cause is corrected (e.g. an allowlist that didn't yet cover the original
        // location, an offline source drive, a temporarily-locked file). Marking an all-skipped run
        // spent would permanently foreclose the only recovery path and strand the file at its new
        // location. Re-running undo is inherently safe: each already-restored row finds its old slot
        // occupied and skips (no clobber), so a retry only acts on the entries that still need it.
        if (run.Undone > 0)
        {
            await revertLog.MarkLastBatchConsumedAsync(summary.Value.RunId, ct);
        }

        return Results.Ok(new UndoResult(
            run.Undone,
            [.. run.Failed.Select(f => new UndoEntryError(f.FileId, f.OldPath, f.NewPath, f.Reason))],
            [.. run.Skipped.Select(s => new UndoEntryError(s.FileId, s.OldPath, s.NewPath, s.Reason))]));
    }

    /// <summary>
    /// Records the outcome of reverse-replaying a batch to the host log: a line per restored file
    /// (current → original), per skip/failure (with reason), and a summary. The restored entries are
    /// the batch rows that are NOT in the failed/skipped buckets (the run result only returns the
    /// restored COUNT, so they are derived here by difference).
    /// <para>
    /// The difference is keyed on the full ROW IDENTITY <c>(FileId, OldPath, NewPath)</c> — the
    /// same triple the <see cref="UndoReplayer.UndoFailure"/> buckets carry — NOT on FileId alone. A
    /// single batch can legitimately contain two rows with the same FileId (a file renamed twice within
    /// one run, or a duplicated row the tolerant parser admits); keying on FileId alone would drop BOTH
    /// such rows from the restored log when only one was a problem (under-reporting restores), or
    /// mislabel a failed duplicate as restored. The row triple is the row's unique identity within the
    /// batch, so each row is bucketed exactly once and the audit log can never misattribute.
    /// </para>
    /// </summary>
    private void LogUndo(string runId, RevertLog.RevertBatch batch, UndoReplayer.UndoRunResult run)
    {
        var problemRows = run.Failed.Select(f => (f.FileId, f.OldPath, f.NewPath))
            .Concat(run.Skipped.Select(s => (s.FileId, s.OldPath, s.NewPath)))
            .ToHashSet();

        foreach (var entry in batch.Entries)
        {
            if (problemRows.Contains((entry.FileId, entry.OldPath, entry.NewPath)))
            {
                continue;
            }

            LogUndoRestored(runId, batch.Kind, entry.EntityId, entry.NewPath, entry.OldPath);
        }

        foreach (var s in run.Skipped)
        {
            LogUndoSkipped(runId, s.FileId, s.Reason);
        }

        foreach (var f in run.Failed)
        {
            LogUndoFailed(runId, f.FileId, f.Reason);
        }

        LogUndoDone(runId, run.Undone, run.Skipped.Count, run.Failed.Count);
    }

    /// <summary>
    /// Returns the paths-free summary of the most recent batch for the undo panel: its
    /// row count, open timestamp, and consumed flag — no paths. Enforces <c>videos.read</c>
    /// in-handler (403-first; minimal-API <c>[RequiresPermission]</c> is inert). An empty log
    /// returns <see cref="LastBatchSummary"/> with <c>HasBatch:false</c>.
    /// </summary>
    internal async Task<IResult> LastBatchAsync(ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        // This is the undo panel's paths-free "is there a batch to undo?" probe (count + timestamp +
        // consumed flag only — no paths). A user who can renamer ANY kind may see it, so gate on holding
        // ANY renamer-read permission rather than videos.read specifically. The summary does not carry
        // the batch kind, so a per-kind gate would require reading the full batch for a metadata probe.
        bool canReadAny = principal.Current is not null
            && (principal.Current.Has(Permissions.VideosRead)
                || principal.Current.Has(Permissions.ImagesRead)
                || principal.Current.Has(Permissions.AudiosRead));
        if (!canReadAny)
        {
            return Results.Json(new { code = "FORBIDDEN" }, statusCode: 403);
        }

        var summary = await new RevertLog(Store).ReadLastBatchSummaryAsync(ct);
        return Results.Ok(new LastBatchSummary(
            HasBatch: summary is not null,
            Count: summary?.Count ?? 0,
            WrittenAtUtcTicks: summary?.WrittenAtUtcTicks ?? 0,
            Consumed: summary?.Consumed ?? false));
    }

    /// <summary>The lightweight id+name projection a routing/exclude picker resolves a name to a stable id against.</summary>
    internal readonly record struct EntityRef(int Id, string Name);

    /// <summary>
    /// Lists the library's studios as id+name for the picker. Gated on holding ANY renamer-read
    /// permission — these are library-wide reference lists, not per-kind data, so the coarse any-read
    /// gate matches the sibling read endpoint (<see cref="LastBatchAsync"/>) rather than a specific
    /// kind's read. Read AsNoTracking so the live library rows are never written back.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Kept as an instance method to match its sibling endpoint handlers and the test " +
            "call sites that invoke it through an extension instance.")]
    internal async Task<IResult> ListStudiosAsync(
        DbContext db, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        // 403 FIRST — before any DB query, so an unauthorized caller reads no rows.
        if (!HasAnyReadPermission(principal))
        {
            return Results.Json(new { code = "FORBIDDEN" }, statusCode: 403);
        }

        // Set<Studio>() reads through the base DbContext seam (the data port binds the base type, not the
        // host's concrete context, which this project does not reference); the projection returns ONLY
        // id+name so no other Studio column can leak.
        var rows = await db.Set<Studio>().AsNoTracking()
            .OrderBy(s => s.Name)
            .Select(s => new EntityRef(s.Id, s.Name))
            .ToArrayAsync(ct);

        return Results.Json(rows, PreviewResponseJsonOptions);
    }

    /// <summary>
    /// Lists the library's tags as id+name for the picker. Same any-read gate, AsNoTracking read, and
    /// id+name-only projection as <see cref="ListStudiosAsync"/>.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Kept as an instance method to match its sibling endpoint handlers and the test " +
            "call sites that invoke it through an extension instance.")]
    internal async Task<IResult> ListTagsAsync(
        DbContext db, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (!HasAnyReadPermission(principal))
        {
            return Results.Json(new { code = "FORBIDDEN" }, statusCode: 403);
        }

        var rows = await db.Set<Tag>().AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new EntityRef(t.Id, t.Name))
            .ToArrayAsync(ct);

        return Results.Json(rows, PreviewResponseJsonOptions);
    }

    /// <summary>
    /// Lists the library's performers as id+name for the picker. Same any-read gate, AsNoTracking read,
    /// and id+name-only projection as <see cref="ListStudiosAsync"/>.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Kept as an instance method to match its sibling endpoint handlers and the test " +
            "call sites that invoke it through an extension instance.")]
    internal async Task<IResult> ListPerformersAsync(
        DbContext db, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (!HasAnyReadPermission(principal))
        {
            return Results.Json(new { code = "FORBIDDEN" }, statusCode: 403);
        }

        var rows = await db.Set<Performer>().AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => new EntityRef(p.Id, p.Name))
            .ToArrayAsync(ct);

        return Results.Json(rows, PreviewResponseJsonOptions);
    }

    private static bool HasAnyReadPermission(ICurrentPrincipalAccessor principal)
        => principal.Current is not null
            && (principal.Current.Has(Permissions.VideosRead)
                || principal.Current.Has(Permissions.ImagesRead)
                || principal.Current.Has(Permissions.AudiosRead));

    private static bool HasAnyWritePermission(ICurrentPrincipalAccessor principal)
        => principal.Current is not null
            && (principal.Current.Has(Permissions.VideosWrite)
                || principal.Current.Has(Permissions.ImagesWrite)
                || principal.Current.Has(Permissions.AudiosWrite));

    /// <summary>
    /// Every renamable kind, in a fixed iteration order. Gallery is excluded — it is not yet a
    /// renamable kind (<see cref="TryParseKind"/> never produces it, <c>LoadEntityAsync</c> returns
    /// null for it). Shared by the whole-library scan and renamer-library job loops so both iterate
    /// the same three kinds in the same order.
    /// </summary>
    private static readonly RenamerFileKind[] RenamableKinds =
        [RenamerFileKind.Video, RenamerFileKind.Image, RenamerFileKind.Audio];

    /// <summary>
    /// Enqueues the whole-library scan job. Takes an OPTIONAL <see cref="ScanLibraryRequest"/> body
    /// carrying the caller's current options (for a dry run on unsaved edits); with no body it scans the
    /// saved options. Takes NO caller-supplied id array — the candidate ids are server-derived per kind
    /// via <see cref="IRenamerDataPort.LoadAllEntityIdsAsync"/> inside the job, so
    /// <see cref="MaxEntityIdsPerRequest"/> does not apply here (there is nothing for it to bound).
    /// Coarse-gates on ANY renamer-read permission — 403 BEFORE any enqueue — then captures
    /// the principal's held read kinds into the job closure so the job body can apply the SAME per-kind
    /// skip a partial-permission caller would see from <see cref="PreviewAsync"/>, without re-resolving
    /// <see cref="ICurrentPrincipalAccessor"/> from inside the detached job. The scan result is persisted
    /// under the FIXED <see cref="LastScanResultKey"/> (mirroring how <c>RevertLog</c> always targets
    /// "the last batch") rather than a per-jobId key, since the id <c>Enqueue</c> mints is not available
    /// to the job body before <c>Enqueue</c> returns.
    /// </summary>
    internal IResult ScanLibraryEnqueue(ScanLibraryRequest? body, ICurrentPrincipalAccessor principal, IJobService jobs)
    {
        if (!HasAnyReadPermission(principal))
        {
            return Results.Json(new { code = "FORBIDDEN" }, statusCode: 403);
        }

        // Dry-run-on-unsaved-edits: when the caller sends its current options blob, parse it with the
        // SAME tolerant options set OptionsStore uses so the scan interprets it identically to a saved
        // load; a null/blank/corrupt blob falls back to the persisted options (the original no-body
        // behavior). Parsed here at enqueue time, then captured into the detached job closure — the job
        // cannot re-read the request, exactly like readableKinds.
        var overrideOptions = TryParseOptionsOverride(body?.Options);

        var readableKinds = RenamableKinds.Where(k => principal.Current!.Has(PermissionsFor(k).Read)).ToArray();

        var jobId = jobs.Enqueue(
            $"ext:{Id}:scan-library",
            $"[{Name}] Scan library",
            (coreProgress, ct) => RunScanLibraryJobAsync(readableKinds, overrideOptions, new HostProgress(coreProgress), ct),
            exclusive: true);

        return Results.Accepted(value: new { jobId });
    }

    /// <summary>
    /// Parses a caller-supplied options blob for the dry-run override, returning null when the blob is
    /// absent, blank, or unparseable. Mirrors <c>OptionsStore</c>'s tolerant read
    /// (<c>RenamerOptions.JsonOptions</c> + catch <see cref="JsonException"/>): a corrupt override
    /// silently falls back to the saved options rather than failing the scan.
    /// </summary>
    private static RenamerOptions? TryParseOptionsOverride(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RenamerOptions>(optionsJson, RenamerOptions.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads back the whole-library scan's persisted result. Re-checks ANY renamer-read permission
    /// (the same coarse gate as enqueue) and returns the stored <see cref="ScanItem"/>[] from the LAST
    /// completed scan, or 404 when no scan has completed yet (mirrors <see cref="IExtensionStore"/>'s
    /// "absent key" contract — there is no per-jobId tracking, so a 404 also covers "wrong/unknown
    /// jobId", which the caller does not need to distinguish: the frontend only ever asks "is the
    /// scan I started done yet").
    /// <para>
    /// The stored result is written under a FIXED key by whoever last ran the scan, capturing THEIR
    /// readable kinds — a higher-permission scan can hold Image/Audio rows a video-only reader may not
    /// see. So each row is filtered to the kinds the CURRENT caller can read (the same per-kind gate the
    /// scan job applied at enqueue) BEFORE returning, mirroring <see cref="PreviewAsync"/>'s per-kind
    /// permission model. A caller who can read every kind the scan covered sees no change.
    /// </para>
    /// </summary>
    internal async Task<IResult> ScanLibraryResultAsync(ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (!HasAnyReadPermission(principal))
        {
            return Results.Json(new { code = "FORBIDDEN" }, statusCode: 403);
        }

        var json = await Store.GetAsync(LastScanResultKey, ct);
        if (string.IsNullOrEmpty(json))
        {
            return Results.NotFound();
        }

        var items = JsonSerializer.Deserialize<ScanItem[]>(json, PreviewResponseJsonOptions) ?? [];
        var readable = items
            .Where(item => principal.Current!.Has(PermissionsFor(item.Kind).Read))
            .ToArray();
        return Results.Json(readable, PreviewResponseJsonOptions);
    }

    /// <summary>
    /// The whole-library scan job body: for each kind the caller can read, loads every candidate id
    /// (<see cref="IRenamerDataPort.LoadAllEntityIdsAsync"/>) and runs the SAME planner
    /// (<c>RenamerPlanner.PlanAsync</c>) <see cref="PreviewAsync"/> already uses, accumulating a
    /// kind-tagged <see cref="ScanItem"/> per planned file. ZERO disk/DB mutation — no <c>SaveAsync</c>,
    /// no <c>File.Move</c>, exactly as read-only as <see cref="PreviewAsync"/>. The accumulated result is
    /// persisted under <see cref="LastScanResultKey"/> for <see cref="ScanLibraryResultAsync"/> to read
    /// back, since <c>JobInfo</c> has no generic result field.
    /// </summary>
    /// <param name="readableKinds">
    /// The kinds the enqueuing principal held read permission for, captured at enqueue time — the job
    /// runs detached from the original request, so this is the only way the per-kind skip (Pitfall 2:
    /// a partial-permission caller's scan must omit a kind they cannot read, never 403 the whole job)
    /// reaches the job body.
    /// </param>
    /// <param name="overrideOptions">
    /// The caller's current options for a dry run on unsaved edits, or null to scan the saved options.
    /// Captured at enqueue time (the detached job cannot re-read the request body).
    /// </param>
    /// <param name="progress">The job-progress sink reported a final <c>1.0</c> on completion.</param>
    /// <param name="ct">Cancellation token; a genuine cancellation aborts the scan.</param>
    internal async Task RunScanLibraryJobAsync(
        IReadOnlyList<RenamerFileKind> readableKinds, RenamerOptions? overrideOptions,
        Cove.Plugins.IJobProgress progress, CancellationToken ct)
    {
        // A dry run previews the caller's CURRENT (possibly unsaved) options when they were sent;
        // otherwise it scans the saved options — the original behavior.
        var options = overrideOptions ?? await new OptionsStore(Store).LoadAsync(ct);
        var lookups = BuildLookups(options);
        var allItems = new List<ScanItem>();

        await using var scope = ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        var port = new CoveRenamerDataPort(db);
        var planner = new RenamerPlanner(port);

        // Load every kind's ids up front so the TOTAL is known before planning — the scan previously
        // reported only a single Report(1.0) at the end, so the job jumped 0%→100% with no intermediate
        // feedback. A denominator lets each planned entity advance the bar. The id-only queries are cheap
        // (they were already run one-per-kind below; this just hoists them so the total is available).
        var idsByKind = new List<(RenamerFileKind Kind, IReadOnlyList<int> Ids)>(readableKinds.Count);
        foreach (var kind in readableKinds)
        {
            ct.ThrowIfCancellationRequested();
            idsByKind.Add((kind, await port.LoadAllEntityIdsAsync(kind, ct)));
        }

        int total = idsByKind.Sum(k => k.Ids.Count);
        LogScanStarted(total, idsByKind.Count);

        // total can be 0 (an empty library / no readable kinds): guard the divisor and report 1.0 so the
        // UI completes instead of dividing by zero or hanging at 0%.
        if (total == 0)
        {
            await Store.SetAsync(LastScanResultKey, JsonSerializer.Serialize(allItems, PreviewResponseJsonOptions), ct);
            LogScanDone(0, 0);
            progress.Report(1d, "Scan complete — nothing to scan.");
            return;
        }

        int done = 0;
        foreach (var (kind, ids) in idsByKind)
        {
            // The scan previously issued one heavy multi-Include query per entity (100K entities = 100K
            // sequential round-trips — the scan bottleneck). Batch-load the kind's entities instead:
            // the port chunks the load internally (CoveRenamerDataPort.LoadChunkSize, one round-trip per
            // ~200 ids), collapsing N round-trips to ~N/chunk. The single chunk-size decision stays in
            // the port; the scan just re-orders the (DB-unordered) result by its own id list so the
            // per-id ORDER and the per-entity progress cadence are preserved — scan output + progress
            // semantics are unchanged, only the LOAD is batched (never the progress).
            var loaded = await port.LoadEntitiesAsync(kind, ids, ct);
            var byId = loaded.ToDictionary(e => e.EntityId);

            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();

                // A missing entry means the id vanished between the id-list query and the batch load —
                // it contributes nothing, matching the old path where PlanAsync on a missing id yielded
                // an empty plan.
                if (byId.TryGetValue(id, out var entity))
                {
                    var plan = await planner.PlanLoadedEntity(entity, options, lookups, ct);
                    allItems.AddRange(plan.Items.Select(item => ScanItem.From(kind, plan.EntityId, item)));
                }

                done++;
                LogScanItemPlanned(done, total, kind, id);
                // Report the fraction planned with a live message, capped just under 1.0 — the final 1.0
                // is reserved for after the result is persisted, so the UI only reads "complete" once the
                // scan result is actually available to fetch.
                progress.Report(Math.Min((double)done / total, 0.99), $"Scanning library… {done}/{total}");
            }
        }

        var json = JsonSerializer.Serialize(allItems, PreviewResponseJsonOptions);
        await Store.SetAsync(LastScanResultKey, json, ct);

        LogScanDone(allItems.Count, total);
        progress.Report(1d, "Scan complete.");
    }

    /// <summary>
    /// Enqueues the whole-library renamer job. Takes NO request body and NO caller-supplied id array
    /// (same rationale as <see cref="ScanLibraryEnqueue"/>: <see cref="MaxEntityIdsPerRequest"/> does
    /// not apply). Coarse-gates on ANY renamer-write permission — 403 BEFORE any enqueue — then captures
    /// the principal's held write kinds into the job closure (the job runs detached from the request, so
    /// it cannot re-resolve <see cref="ICurrentPrincipalAccessor"/> itself).
    /// </summary>
    internal IResult RenamerLibraryEnqueue(ICurrentPrincipalAccessor principal, IJobService jobs)
    {
        if (!HasAnyWritePermission(principal))
        {
            return Results.Json(new { code = "FORBIDDEN" }, statusCode: 403);
        }

        var writableKinds = RenamableKinds.Where(k => principal.Current!.Has(PermissionsFor(k).Write)).ToArray();

        var jobId = jobs.Enqueue(
            $"ext:{Id}:renamer-library",
            $"[{Name}] Renamer library",
            (coreProgress, ct) => RunRenamerLibraryJobAsync(writableKinds, new HostProgress(coreProgress), ct),
            exclusive: true);

        return Results.Accepted(value: new { jobId });
    }

    /// <summary>
    /// The whole-library renamer job body: for each kind the caller can write, loads every candidate id
    /// and — when the list is non-empty — calls the EXISTING <see cref="RunRenamerBatchAsync"/> ONCE for
    /// that kind, exactly as <c>/renamer</c> already does for a single-kind selection. A kind with zero
    /// candidate ids is skipped entirely (no call into <see cref="RunRenamerBatchAsync"/> for it), so no
    /// empty <c>RevertLog</c> batch header opens for it — matching that method's own "nothing acts → no
    /// batch" behavior. Never combines kinds into one call: <c>RevertLog</c>'s batch header is one
    /// <see cref="RenamerFileKind"/> per batch by design, so a whole-library renamer across all three kinds
    /// naturally opens up to three separate batches/runIds, one per acting kind — this introduces NO
    /// multi-kind batch format and NO engine/executor/<c>RevertLog</c> change. A consequence worth
    /// noting (not fixed here, out of scope): <c>/undo</c> only replays the single LAST open batch, so if
    /// this run touches more than one kind, only the last kind's batch is undoable via the existing
    /// single-shot Undo button.
    /// </summary>
    /// <param name="writableKinds">The kinds the enqueuing principal held write permission for, captured at enqueue time (same rationale as <see cref="RunScanLibraryJobAsync"/>'s <c>readableKinds</c> parameter).</param>
    /// <param name="progress">The job-progress sink, forwarded into each per-kind <see cref="RunRenamerBatchAsync"/> call.</param>
    /// <param name="ct">Cancellation token; a genuine cancellation aborts the remaining kinds.</param>
    internal async Task RunRenamerLibraryJobAsync(
        IReadOnlyList<RenamerFileKind> writableKinds, Cove.Plugins.IJobProgress progress, CancellationToken ct)
    {
        foreach (var kind in writableKinds)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<int> ids;
            await using (var scope = ScopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DbContext>();
                ids = await new CoveRenamerDataPort(db).LoadAllEntityIdsAsync(kind, ct);
            }

            if (ids.Count == 0)
            {
                continue;
            }

            LogLibraryKind(kind, ids.Count);

            var parameters = RenamerJob.Encode(EntityTypeFor(kind), ids);
            await RunRenamerBatchAsync(parameters, progress, ct);
        }

        progress.Report(1d, "Library renamer complete.");
    }

    /// <summary>
    /// The reverse of <see cref="TryParseKind"/>: maps a <see cref="RenamerFileKind"/> back to the
    /// lowercase-singular Cove entity-type string <see cref="RenamerJob.Encode"/> expects. Only the three
    /// renamable kinds round-trip (Gallery never reaches this method — <see cref="RenamableKinds"/>
    /// excludes it).
    /// </summary>
    private static string EntityTypeFor(RenamerFileKind kind) => kind switch
    {
        RenamerFileKind.Video => "video",
        RenamerFileKind.Image => "image",
        RenamerFileKind.Audio => "audio",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a renamable kind"),
    };

    /// <summary>
    /// The live-preview endpoint: runs the REAL <see cref="TemplateEngine"/> over the
    /// fixed <see cref="SampleTokenSets"/> with the in-flight options from the request body and returns
    /// one <see cref="PreviewSampleResult"/> per sample (old→new + folder + advisory flags). Pure and
    /// selection-less — NO planner, DB, or disk (so a hostile template cannot escape or amplify).
    /// Enforces <c>videos.read</c> in-handler BEFORE any body read or engine work (minimal-API
    /// <c>[RequiresPermission]</c> is inert — mirrors <see cref="PreviewAsync"/>).
    /// <para>
    /// The body is deserialized with <see cref="RenamerOptions.JsonOptions"/> (case-insensitive +
    /// <c>JsonStringEnumConverter</c>) rather than the host's default minimal-API options, which lack
    /// the enum converter — so a panel body carrying string enum values (<c>"case":"Lower"</c>,
    /// <c>"onOverflow":"KeepFirst"</c>, <c>"sort":"NameAsc"</c>) deserializes instead of 400ing. Empty
    /// or <c>null</c>-Options body → safe defaults; MALFORMED JSON → 400.
    /// </para>
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Kept as an instance method to match its sibling endpoint handlers " +
            "(PreviewAsync/RenamerEnqueue/UndoAsync/LastBatchAsync) and the test call sites that invoke " +
            "it through an extension instance; making it static would churn those call sites without " +
            "any behavior change.")]
    internal async Task<IResult> PreviewSampleAsync(
        HttpRequest httpReq, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        // Enforce permission BEFORE touching the body — never read/parse for an unauthorized caller.
        // The sample preview is a pure template render over fixed Video/Image/Audio samples (no DB, no
        // selection), so gate on holding ANY renamer-read permission rather than videos.read specifically.
        bool canReadAny = principal.Current is not null
            && (principal.Current.Has(Permissions.VideosRead)
                || principal.Current.Has(Permissions.ImagesRead)
                || principal.Current.Has(Permissions.AudiosRead));
        if (!canReadAny)
        {
            return Results.Json(new { code = "FORBIDDEN" }, statusCode: 403);
        }

        // Read the body to a string first so we can distinguish "no content" (→ defaults) from
        // "content present but malformed" (→ 400). System.Text.Json throws on a zero-length stream.
        string body;
        using (var reader = new StreamReader(httpReq.Body, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync(ct);
        }

        RenamerOptions? options;
        if (string.IsNullOrWhiteSpace(body))
        {
            // Empty/whitespace body → safe defaults, not a 400.
            options = null;
        }
        else
        {
            PreviewSampleRequest? req;
            try
            {
                // Converter-aware parse: case-insensitive props + JsonStringEnumConverter, so a body
                // carrying string enum values deserializes instead of 400ing on the host's default opts.
                req = JsonSerializer.Deserialize<PreviewSampleRequest>(body, RenamerOptions.JsonOptions);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { code = "INVALID_BODY" });
            }

            // Null Options (e.g. {"Options":null} or {}) → defaults; unknown JSON props ignored on parse.
            options = req?.Options;
        }

        options ??= new RenamerOptions();

        var results = SampleTokenSets.All
            .Select(sample => RenderSample(sample, options))
            .ToList();

        return Results.Ok(results);
    }

    /// <summary>
    /// Renders one sample through the engine and derives the advisory flags:
    /// <list type="bullet">
    ///   <item><c>empty</c> — the rendered name has no name component.</item>
    ///   <item><c>sanitized</c> — the engine's sanitize step changed the name (illegal chars
    ///     stripped/replaced or spaces replaced) under the active options.</item>
    ///   <item><c>length-reduced</c> — the length reducer dropped one or more fields; the dropped
    ///     names come straight from the engine, never a string diff.</item>
    ///   <item><c>gating-skip</c> — a <see cref="RenamerOptions.RequiredFields"/> token resolves empty
    ///     for this sample, so a real renamer would skip it.</item>
    /// </list>
    /// </summary>
    private static PreviewSampleResult RenderSample(SampleTokenSets.Sample sample, RenamerOptions options)
    {
        var (result, dropped) = TemplateEngine.RenderWithDropped(
            sample.Tokens, sample.MultiValues, options);

        var flags = new List<string>();

        if (result.Filename.Length == 0)
        {
            flags.Add("empty");
        }

        if (TemplateEngine.WouldSanitizeFilename(sample.Tokens, sample.MultiValues, options))
        {
            flags.Add("sanitized");
        }

        if (dropped.Count > 0)
        {
            flags.Add("length-reduced");
        }

        bool gated = options.RequiredFields.Any(field =>
            TemplateEngine.ResolveField(sample.Tokens, sample.MultiValues, options, field).Length == 0);
        if (gated)
        {
            flags.Add("gating-skip");
        }

        string newName = result.Filename + result.Ext;

        return new PreviewSampleResult(
            SampleLabel: sample.Label,
            OldName: sample.OldName,
            NewName: newName,
            Folder: result.FolderPath,
            Flags: flags.ToArray(),
            DroppedFields: dropped.ToArray());
    }
}
