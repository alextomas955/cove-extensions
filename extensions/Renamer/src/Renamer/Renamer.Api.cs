using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Api;
using Renamer.Contracts;
using Renamer.Engine;
using Renamer.Execution;
using Renamer.Jobs;
using Renamer.Options;
using Renamer.Planner;
using static Cove.Extensions.Shared.MinimalApiPermissions;
using static Renamer.Contracts.PreviewContracts;

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
    //
    // Instance members because Id comes from extension.json: reading a route before the host has
    // applied the manifest throws instead of mounting the endpoints under the wrong id.
    private string RouteBase => "/api/extensions/" + Id;
    private string RenamerRoute => RouteBase + "/renamer";
    private string PreviewRoute => RouteBase + "/preview";
    private string PreviewSampleRoute => RouteBase + "/preview-sample";
    private string UndoRoute => RouteBase + "/undo";
    private string LastBatchRoute => RouteBase + "/last-batch";
    private string ScanLibraryRoute => RouteBase + "/scan-library";
    private string LastScanRoute => RouteBase + "/last-scan";
    private string ScanRowsRoute => RouteBase + "/scan-rows";
    private string RenamerLibraryRoute => RouteBase + "/renamer-library";
    private string LibraryPathsRoute => RouteBase + "/library-paths";
    private string JobStatusRoute => RouteBase + "/job-status/{jobId}";
    private string OrphanedRulesRoute => RouteBase + "/orphaned-rules";

    /// <summary>
    /// The key a pre-0.2.1 scan wrote one wire row PER FILE to. Retained only so
    /// <see cref="InitializeAsync"/> can delete it; nothing reads it.
    /// </summary>
    internal const string LastScanResultKey = "last-scan-result";

    /// <summary>The fixed <see cref="IExtensionStore"/> key the whole-library scan's bounded aggregate lives under.</summary>
    internal const string LastScanSummaryKey = "last-scan-summary";

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
    /// <summary>
    /// Registers every endpoint, each declaring the access intent they share: an authenticated caller,
    /// with the permission itself decided in the handler.
    /// </summary>
    /// <remarks>
    /// An endpoint carrying none of the SDK's authorization conventions allows ANONYMOUS access for
    /// backward compatibility, and the host warns about it by name at every registration. So the
    /// declaration is the point here rather than a change of who may call: each handler already checks
    /// the caller and answers <c>403</c>.
    /// <para>
    /// Which declaration an endpoint gets is decided by its REACH, not by its verb. An endpoint whose
    /// subject is the whole library - scanning it, renaming it, serving that scan's rows, or reversing a
    /// batch of unknown extent - has no "which item" for the host to authorize, so "may this caller
    /// touch everything" is the honest question and it is asked with a concrete policy. The host answers
    /// it from an unrestricted grant, which is the point: a caller who can see part of the library has no
    /// business renaming all of it.
    /// </para>
    /// <para>
    /// The rest keep the authenticated-caller declaration, because a concrete policy would ask the wrong
    /// question of them. Some name their items in the request BODY as a list, which no route-bound entity
    /// check can express, so authorizing those ids is this extension's own job and the handler does it
    /// per kind. The others - job progress, which of the caller's own rules are broken, the configured
    /// library paths, the fixed sample render - disclose no library content at all, and demanding an
    /// unrestricted grant for them would lock out scoped accounts for nothing.
    /// </para>
    /// </remarks>
    public override void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(PreviewRoute,
            (RenamerRequest req, DbContext db, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => PreviewAsync(req, db, principal, ct))
            .AllowWithoutCovePermission();

        endpoints.MapPost(RenamerRoute,
            (RenamerRequest req, ICurrentPrincipalAccessor principal, IJobService jobs)
                => RenamerEnqueue(req, principal, jobs))
            .AllowWithoutCovePermission();

        // NB: this endpoint binds the RAW HttpContext (not a typed PreviewSampleRequest) so the
        // handler can deserialize the body with RenamerOptions.JsonOptions — the host's default
        // minimal-API JsonSerializerOptions has NO JsonStringEnumConverter, so a body carrying
        // string enum values (e.g. "case":"Lower") would 400 on typed binding before the handler
        // ran. Extension code cannot touch host startup (ConfigureHttpJsonOptions), so we parse
        // the body ourselves with the converter-aware options.
        // The handler reads the raw request so it can parse the options blob with the extension's own
        // tolerant serializer rather than the host's. No parameter therefore declares the body, and
        // without .Accepts<> the emitted document carries no request schema for this route at all -
        // which also silently exempts that body from the drift check the document exists for.
        endpoints.MapPost(PreviewSampleRoute,
            (HttpContext http, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => PreviewSampleAsync(http.Request, principal, ct))
            .Accepts<PreviewSampleRequest>("application/json")
            .AllowWithoutCovePermission();

        // /undo takes NO request body — it operates on "the last batch", so binding no body avoids
        // the host's enum-converter 400 trap (see the preview-sample note above); /last-batch is a plain read.
        endpoints.MapPost(UndoRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => UndoAsync(principal, ct))
            .RequireCovePermission(
                PermissionMode.Any,
                Permissions.VideosWrite, Permissions.ImagesWrite, Permissions.AudiosWrite);

        endpoints.MapGet(LastBatchRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => LastBatchAsync(principal, ct))
            .AllowWithoutCovePermission();

        endpoints.MapPost(ScanLibraryRoute,
            (ScanLibraryRequest? body, ICurrentPrincipalAccessor principal, IJobService jobs) =>
                ScanLibraryEnqueue(body, principal, jobs))
            .RequireCovePermission(
                PermissionMode.Any,
                Permissions.VideosRead, Permissions.ImagesRead, Permissions.AudiosRead);

        endpoints.MapGet(LastScanRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => ScanLibraryResultAsync(principal, ct))
            .RequireCovePermission(
                PermissionMode.Any,
                Permissions.VideosRead, Permissions.ImagesRead, Permissions.AudiosRead);

        endpoints.MapPost(ScanRowsRoute,
            (ScanRowsRequest? body, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => ScanRowsAsync(body, principal, ct))
            .RequireCovePermission(
                PermissionMode.Any,
                Permissions.VideosRead, Permissions.ImagesRead, Permissions.AudiosRead);

        endpoints.MapPost(RenamerLibraryRoute,
            (ICurrentPrincipalAccessor principal, IJobService jobs) => RenamerLibraryEnqueue(principal, jobs))
            .RequireCovePermission(
                PermissionMode.Any,
                Permissions.VideosWrite, Permissions.ImagesWrite, Permissions.AudiosWrite);

        endpoints.MapGet(LibraryPathsRoute,
            (ICurrentPrincipalAccessor principal) => LibraryPaths(principal))
            .AllowWithoutCovePermission();

        endpoints.MapGet(JobStatusRoute,
            (string jobId, ICurrentPrincipalAccessor principal, IJobService jobs)
                => JobStatus(jobId, principal, jobs))
            .AllowWithoutCovePermission();

        endpoints.MapGet(OrphanedRulesRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct)
                => OrphanedRulesAsync(principal, ct))
            .AllowWithoutCovePermission();
    }

    /// <summary>
    /// Cove's configured library paths - the list every destination root is CHOSEN from, so the
    /// settings panel offers exactly the roots the planner will accept and no typed path exists to
    /// drift from them.
    /// </summary>
    /// <remarks>
    /// Reads the host configuration this extension already holds; it opens no scope and touches no
    /// database, because the answer is in-memory host settings rather than library data. Gated on
    /// holding ANY renamer-read permission, like <c>/last-batch</c>: the list says where a rename may
    /// write, not what the library contains.
    /// </remarks>
    internal Results<Ok<LibraryPathsView>, ForbiddenCode> LibraryPaths(
        ICurrentPrincipalAccessor principal)
        => HasAnyReadPermission(principal)
            ? TypedResults.Ok(new LibraryPathsView(LibraryRoots))
            : new ForbiddenCode();

    /// <summary>The prefix the host mints onto every job type this extension enqueues.</summary>
    private string OwnJobTypePrefix => "ext:" + Id + ":";

    /// <summary>
    /// Where one of this extension's own runs has got to.
    /// </summary>
    /// <remarks>
    /// The panel cannot read the host's job route: Cove gates it on unrestricted read, so a scoped
    /// account is refused there even for a run it started itself. This serves the same few fields from
    /// <see cref="IJobService"/> under the extension's own permission check, which is why it is a
    /// minimal-API route — the host's MVC access filter does not reach one.
    /// <para>
    /// A job whose type does not carry <see cref="OwnJobTypePrefix"/> is reported as NOT FOUND rather
    /// than forbidden. Answering "forbidden" would confirm that id names a real job, which is the fact
    /// the host's own gate withholds; reporting on it at all would make this route a way around that
    /// gate rather than a replacement for the part of it this extension owns.
    /// </para>
    /// </remarks>
    internal Results<Ok<RenamerJobStatus>, NotFound, ForbiddenCode> JobStatus(
        string jobId, ICurrentPrincipalAccessor principal, IJobService jobs)
    {
        if (!HasAnyReadPermission(principal))
        {
            return new ForbiddenCode();
        }

        var job = jobs.GetJob(jobId);
        if (job is null || !job.Type.StartsWith(OwnJobTypePrefix, StringComparison.Ordinal))
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(RenamerJobStatus.From(job));
    }

    /// <summary>
    /// Which per-studio and per-tag rule keys name an entity Cove no longer holds.
    /// </summary>
    /// <remarks>
    /// Read as System, because the question is whether the entity EXISTS and a caller who cannot see it
    /// would otherwise be told it is gone. Nothing about the entity is returned — only which of the
    /// caller's own rule keys no longer resolve — so elevating discloses no more than the rules the
    /// caller already holds.
    /// <para>
    /// Asks about exactly the ids the rules name, so both the query and the answer are bounded by how
    /// many rules the user wrote rather than by library size. No rules means no query at all.
    /// </para>
    /// </remarks>
    internal async Task<Results<Ok<OrphanedRulesView>, ForbiddenCode>> OrphanedRulesAsync(
        ICurrentPrincipalAccessor principal, CancellationToken ct = default)
    {
        if (!HasAnyReadPermission(principal))
        {
            return new ForbiddenCode();
        }

        var options = await new OptionsStore(Store, _log).LoadAsync(ct);
        int[] studioIds = [.. options.StudioDestinations.Keys];
        int[] tagIds = [.. options.TagDestinations.Keys];

        if (studioIds.Length == 0 && tagIds.Length == 0)
        {
            return TypedResults.Ok(new OrphanedRulesView([], []));
        }

        (int[] missingStudios, int[] missingTags) = await RunAsSystem.RunInSystemScopeAsync(
            ScopeFactory,
            async services =>
            {
                var db = services.GetRequiredService<DbContext>();

                List<int> liveStudios = studioIds.Length == 0
                    ? []
                    : await db.Set<Cove.Core.Entities.Studio>()
                        .Where(x => studioIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
                List<int> liveTags = tagIds.Length == 0
                    ? []
                    : await db.Set<Cove.Core.Entities.Tag>()
                        .Where(x => tagIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);

                return (
                    Studios: studioIds.Except(liveStudios).Order().ToArray(),
                    Tags: tagIds.Except(liveTags).Order().ToArray());
            });

        return TypedResults.Ok(new OrphanedRulesView(missingStudios, missingTags));
    }

    /// <summary>
    /// The synchronous, read-only dry-run: runs the planner over each requested
    /// id and returns the accumulated <see cref="RenamerPlanItem"/>[] (old→new + status) — ZERO
    /// mutation. Enforces <c>videos.read</c> in-handler because the host's <c>[RequiresPermission]</c>
    /// filter is MVC-only and inert on minimal-API endpoints.
    /// </summary>
    internal async Task<Results<Ok<PreviewResponse>, BadRequest<ErrorCode>, ForbiddenCode>> PreviewAsync(
        RenamerRequest req, DbContext db, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        // Resolve the kind FIRST so the permission check below gates on the request's own entity kind
        // (videos/images/audios.read) rather than always videos.read. An unparseable kind is a 400
        // before the auth check leaks nothing — it carries no ids and reads no data either way.
        if (!TryParseKind(req.EntityType, out var kind))
        {
            return TypedResults.BadRequest(new ErrorCode("UNSUPPORTED_ENTITY_TYPE"));
        }

        var (readPermission, _) = PermissionsFor(kind);
        if (Forbidden(principal, readPermission) is { } denied)
        {
            return denied;
        }

        // Reject an oversized id array before any per-id DB work (see MaxEntityIdsPerRequest).
        if (req.EntityIds.Length > MaxEntityIdsPerRequest)
        {
            return TypedResults.BadRequest(new ErrorCode("TOO_MANY_IDS", MaxEntityIdsPerRequest));
        }

        var options = await new OptionsStore(Store, _log).LoadAsync(ct);
        var port = new CoveRenamerDataPort(db, _coveConfig);
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

        // The whole-batch blast radius: a pure aggregate over the acting items + their sizes. The path
        // budget is read from the loaded options ONCE and handed to both halves of the response below, so
        // the aggregate's in-flight overflow COUNT and the per-item FLAGS cannot be measured against
        // different limits and disagree.
        var summary = BatchPreview.Summarize(items, sizeByFileId, options.FullPathMax);

        // The host's serializer is camelCase but emits NUMERIC enums (status:0), which the frontend's
        // buildConfirmSummary reads as a non-renamer — so the renamer would silently never fire. The
        // string spelling comes from CamelCaseStringEnumConverter declared ON RenamerStatus and
        // ConfirmLevel, never from an options instance chosen here.
        return TypedResults.Ok(
            new PreviewResponse(
                [.. items.Select(i => PreviewItemView.From(
                    i, BatchPreview.InFlightPathOverflows(i, options.FullPathMax)))],
                summary));
    }

    /// <summary>
    /// Enqueues the batch-renamer job: encodes the request into the job params and hands the host
    /// a delegate that adapts the core <see cref="Cove.Core.Interfaces.IJobProgress"/> via <c>HostProgress</c> and
    /// calls the shared <see cref="RunRenamerBatchAsync"/>. Returns 202 {jobId}. Re-checks
    /// <c>videos.write</c> in-handler (the host permission filter is inert on minimal-API endpoints)
    /// — and crucially returns 403 BEFORE any enqueue.
    /// </summary>
    internal Results<Accepted<JobEnqueued>, BadRequest<ErrorCode>, ForbiddenCode> RenamerEnqueue(
        RenamerRequest req, ICurrentPrincipalAccessor principal, IJobService jobs)
    {
        // Kind first so the write check gates on the request's own kind (videos/images/audios.write).
        if (!TryParseKind(req.EntityType, out var kind))
        {
            return TypedResults.BadRequest(new ErrorCode("UNSUPPORTED_ENTITY_TYPE"));
        }

        var (_, writePermission) = PermissionsFor(kind);
        if (Forbidden(principal, writePermission) is { } denied)
        {
            return denied;
        }

        // Reject an oversized id array before encoding/enqueuing the job (see MaxEntityIdsPerRequest).
        if (req.EntityIds.Length > MaxEntityIdsPerRequest)
        {
            return TypedResults.BadRequest(new ErrorCode("TOO_MANY_IDS", MaxEntityIdsPerRequest));
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

        return TypedResults.Accepted((string?)null, new JobEnqueued(jobId));
    }

    /// <summary>
    /// Reverse-replays the most recent renamer batch. Enforces <c>videos.write</c>
    /// in-handler and returns 403 BEFORE any journal read / scope open / disk touch (the
    /// host's <c>[RequiresPermission]</c> filter is inert on minimal-API endpoints; mirrors
    /// <see cref="RenamerEnqueue"/>). Takes no body — it always targets "the batch with rows left".
    /// <para>
    /// Names its target through <see cref="IRevertJournal.ReadUndoTargetAsync"/> — the same read the
    /// panel's summary uses, so the batch described and the batch acted on cannot be different ones —
    /// then reads that batch's rows A PAGE AT A TIME and reverse-replays each page via
    /// <see cref="UndoReplayer"/> (kind from the batch, entityId from each row — there is NO hardcoded
    /// Video default on this path). Every row whose outcome is settled is RETIRED: one that was
    /// restored, and one that stopped for the single reason no retry can improve on. A row that stopped
    /// for any other reason stays in the journal, so a later <c>/undo</c> retries exactly the work that
    /// is still outstanding, and a batch with nothing left is not offered again. A null/empty batch is a
    /// clean <c>{undone:0}</c> no-op.
    /// </para>
    /// <para>
    /// Paging bounds this handler's READ memory by the page size rather than by the batch. Each page is
    /// handed to the replayer wrapped in the same <see cref="RevertBatch"/> record it already consumed:
    /// the replayer's signature, its per-entity path cache and its whole restore spine are deliberately
    /// untouched here. That spine is the destructive path, and a storage change has no business
    /// rewriting it.
    /// </para>
    /// <para>
    /// What bounds the RESPONSE is <see cref="IRevertJournal.MaxJournalledFiles"/>. The two error
    /// buckets accumulate across every page, and the cursor strictly decreases so each row reaches them
    /// at most once — so the reply carries at most one entry per file the batch journalled, and a batch
    /// past the cap journalled none.
    /// </para>
    /// </summary>
    internal async Task<Results<Ok<UndoResult>, ForbiddenCode>> UndoAsync(
        ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        // 403 FIRST for a caller holding NO renamer-write permission of any kind — before any journal
        // read or disk touch, so an unauthorized caller cannot even learn whether a batch exists. The
        // SPECIFIC kind's write permission is re-checked below once the batch reveals the kind; this
        // coarse gate only preserves the "no read/disk work for the wholly-unauthorized" property.
        bool canWriteAny = principal.Current is not null
            && (principal.Current.Has(Permissions.VideosWrite)
                || principal.Current.Has(Permissions.ImagesWrite)
                || principal.Current.Has(Permissions.AudiosWrite));
        if (!canWriteAny)
        {
            return new ForbiddenCode();
        }

        // Open a scope the SAME way RunRenamerBatchAsync does and resolve the scoped DbContext. It is
        // opened before the batch read because the journal now lives in the database rather than in the
        // extension store — a scope open is not a mutation, so the "an under-permissioned caller
        // changes nothing" property the per-kind re-gate below protects is unaffected.
        await using var scope = ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        using var journal = new CoveRevertJournal(db);

        // Retention is enforced here as well as when a batch is opened, because opening one is not the
        // only way the window can be crossed: a library nobody renames for longer than the window keeps
        // its last batch replayable indefinitely, and the panel already refuses that batch on the same
        // constant. A restore the panel says has expired must not succeed for a caller reaching the API
        // directly.
        await journal.PurgeExpiredAsync(DateTime.UtcNow, ct);

        var target = await journal.ReadUndoTargetAsync(ct);
        if (target is null)
        {
            return TypedResults.Ok(new UndoRunAccumulator().ToResult());
        }

        // The first page is read BEFORE the per-kind re-gate, exactly where the whole-batch read used to
        // sit: a settled batch is the "nothing to undo" answer for every caller holding any renamer
        // write permission, not a 403 that would also disclose which kind the settled batch was.
        var page = await journal.ReadBatchPageAsync(
            target.Value.RunId, belowSeq: long.MaxValue, CoveRevertJournal.DefaultPageSize, ct);
        if (page.Count == 0)
        {
            return TypedResults.Ok(new UndoRunAccumulator().ToResult());
        }

        // Re-gate on the WRITE permission of the kind that was actually renamed (the batch carries it)
        // — undoing an image renamer requires images.write, not videos.write. This is checked after the
        // batch read (needed to learn the kind) but BEFORE the options load and any disk touch, so an
        // under-permissioned caller still mutates nothing.
        var (_, undoWritePermission) = PermissionsFor(target.Value.Kind);
        if (Forbidden(principal, undoWritePermission) is { } denied)
        {
            return denied;
        }

        // Load the configured options AFTER the 403-first check and the empty-batch early-return (so an
        // unauthorized or no-op request still short-circuits without the load), mirroring PreviewAsync.
        // The undo RE-GATES each restore target against options.AllowedRoots — the same write boundary
        // the forward move used — so a restore can never land outside the allowed roots.
        var options = await new OptionsStore(Store, _log).LoadAsync(ct);

        var replayer = new UndoReplayer(new CoveRenamerDataPort(db, _coveConfig), EventBus, new DiskMover(),
            cross: new CrossVolumeMover(), allowedRoots: options.AllowedRoots);

        // Each page's outcome is folded into a total plus a bounded sample. Retaining every page's
        // entries would rebuild in this handler's memory - and then on the wire - exactly the
        // library-sized value the paged read above removed. The host log still receives every entry,
        // per page, which is where the full detail belongs.
        var accumulated = new UndoRunAccumulator();

        while (page.Count > 0)
        {
            var pageBatch = new RevertBatch(target.Value.RunId, target.Value.Kind, page);
            var run = await replayer.RevertAsync(pageBatch, ct);

            LogUndoEntries(target.Value.RunId, pageBatch, run);

            accumulated.Add(run);

            // Retire each row whose file actually came back. A row that stopped for a reason the world
            // can clear STAYS, so it is offered again on the next undo — which is what makes a retry act
            // on exactly the work still outstanding after the cause is corrected (an allowlist that
            // didn't yet cover the original location, an offline source drive, a locked file).
            foreach (var row in run.Restored)
            {
                await journal.DeleteRowAsync(row.RunId, row.Seq, unrestorable: false, ct);
            }

            // A row that can NEVER be restored is retired too, on the other counter. Leaving it would
            // keep the batch offering an undo that cannot complete, and the panel promising work that
            // will never happen; the aggregate still records that it ended unrestorable, and the reason
            // was already surfaced in this very response. The decision reads the TYPED stop reason — the
            // same fact as the note beside it, but as a value, so rewording a message for a human cannot
            // change which rows get deleted for good.
            foreach (var stopped in run.Failed.Concat(run.Skipped)
                .Where(stopped => UndoTerminalClassifier.IsTerminal(stopped.Stop)))
            {
                await journal.DeleteRowAsync(stopped.RunId, stopped.Seq, unrestorable: true, ct);
            }

            // The cursor is the LOWEST sequence this page returned, and the next page returns only rows
            // strictly below it — so the cursor strictly decreases and the loop terminates whatever the
            // outcomes were. A cursor that failed to advance would re-read the same page forever, which
            // is a hang rather than an error, so it is pinned by a test rather than left to review.
            page = await journal.ReadBatchPageAsync(
                target.Value.RunId, page[^1].Seq, CoveRevertJournal.DefaultPageSize, ct);
        }

        // The log line reports the run's TOTALS, which are the accumulator's counts and never a sample's
        // length: a host log that under-reported a large undo would be worse than no line at all.
        var result = accumulated.ToResult();
        LogUndoDone(target.Value.RunId, result.Undone, result.SkippedCount, result.FailedCount);

        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Records the outcome of reverse-replaying ONE PAGE of a batch to the host log: a line per
    /// restored file (current → original) and one per skip/failure (with reason).
    /// </summary>
    /// <remarks>
    /// The run's closing summary line is deliberately NOT written here: an undo replays as many pages
    /// as the batch has, and a "done" line per page would report a fraction of the run as the whole of
    /// it. The caller writes it once, from the totals.
    /// <para>
    /// The restored rows are the ones the replayer reports, never a set derived by subtracting the
    /// problem buckets from the batch. A single batch can legitimately hold two rows for the same file
    /// (renamed twice within one run), so a derived set has to reconstruct each row's identity from its
    /// paths to bucket it once — and get that reconstruction exactly right. The replayer already knows
    /// which rows it restored.
    /// </para>
    /// </remarks>
    private void LogUndoEntries(string runId, RevertBatch batch, UndoReplayer.UndoRunResult run)
    {
        foreach (var entry in run.Restored)
        {
            LogUndoRestored(runId, batch.Kind, entry.EntityId, entry.OldPath);
        }

        foreach (var s in run.Skipped)
        {
            LogUndoSkipped(runId, s.FileId, s.Reason);
        }

        foreach (var f in run.Failed)
        {
            LogUndoFailed(runId, f.FileId, f.Reason);
        }

        // Entries that were restored anyway, minus a companion file. They are in no counted bucket, so
        // this loop is the only place a partial restore surfaces in the log.
        foreach (var w in run.Warnings)
        {
            LogUndoSidecarStranded(runId, w.FileId, w.Detail);
        }
    }

    /// <summary>
    /// Returns the paths-free summary of the batch an undo would act on: its original file count, open
    /// timestamp, and spent flag — no paths. Enforces <c>videos.read</c>
    /// in-handler (403-first; minimal-API <c>[RequiresPermission]</c> is inert). An empty journal
    /// returns <see cref="LastBatchSummary"/> with <c>HasBatch:false</c>.
    /// </summary>
    /// <remarks>
    /// A batch is spent when it has no rows left to restore, which is derived from the aggregate rather
    /// than stored: a row exists exactly while its file still needs restoring, so "nothing remains" and
    /// "already undone" are the same fact and cannot disagree.
    /// <para>
    /// Reads the batch row only. It never touches the row table, so the response stays O(1) whatever the
    /// batch's size — which is also what keeps this endpoint's coarse permission gate defensible.
    /// </para>
    /// </remarks>
    internal async Task<Results<Ok<LastBatchSummary>, ForbiddenCode>> LastBatchAsync(
        ICurrentPrincipalAccessor principal, CancellationToken ct)
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
            return new ForbiddenCode();
        }

        // The journal is a database read now, so this endpoint needs the scope it never had.
        await using var scope = ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();

        using var journal = new CoveRevertJournal(db);

        // The SAME read /undo names its target with, which is what makes the line this endpoint feeds
        // describe the batch the button will act on. Two reads that merely agreed today drifted the
        // moment a newer batch could settle while an older one still held rows.
        var summary = await journal.ReadUndoTargetAsync(ct);
        return TypedResults.Ok(new LastBatchSummary(
            HasBatch: summary is not null,
            Count: summary?.OriginalCount ?? 0,
            RemainingCount: summary?.Remaining ?? 0,
            UnrestorableCount: summary?.UnrestorableCount ?? 0,
            WrittenAtUtcTicks: summary?.WrittenAtUtcTicks ?? 0,
            Consumed: summary is not null && summary.Value.Remaining == 0));
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
    /// <see cref="ICurrentPrincipalAccessor"/> from inside the detached job. The scan summary is persisted
    /// under the FIXED <see cref="LastScanSummaryKey"/> (mirroring how <c>RevertLog</c> always targets
    /// "the last batch") rather than a per-jobId key, since the id <c>Enqueue</c> mints is not available
    /// to the job body before <c>Enqueue</c> returns.
    /// </summary>
    internal Results<Accepted<JobEnqueued>, ForbiddenCode> ScanLibraryEnqueue(
        ScanLibraryRequest? body, ICurrentPrincipalAccessor principal, IJobService jobs)
    {
        if (!HasAnyReadPermission(principal))
        {
            return new ForbiddenCode();
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

        return TypedResults.Accepted((string?)null, new JobEnqueued(jobId));
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
    /// Reads back the whole-library scan's persisted aggregate. Re-checks ANY renamer-read permission
    /// (the same coarse gate as enqueue) and returns a <see cref="ScanSummaryView"/> for the LAST
    /// completed scan, or 404 when no scan has completed yet (mirrors <see cref="IExtensionStore"/>'s
    /// "absent key" contract — there is no per-jobId tracking, so a 404 also covers "wrong/unknown
    /// jobId", which the caller does not need to distinguish: the frontend only ever asks "is the
    /// scan I started done yet").
    /// <para>
    /// The stored aggregate is written under a FIXED key by whoever last ran the scan, capturing THEIR
    /// readable kinds — a higher-permission scan can hold Image/Audio figures a video-only reader may
    /// not see. So the merge keeps only the kinds the CURRENT caller can read, mirroring
    /// <see cref="PreviewAsync"/>'s per-kind permission model. A caller who can read every kind the scan
    /// covered sees the whole aggregate.
    /// </para>
    /// <para>
    /// A blob that will not parse, or one stamped with an unrecognised
    /// <see cref="ScanSummary.SchemaVersion"/>, reads as "no scan yet" rather than throwing — the same
    /// tolerant-read posture <see cref="TryParseOptionsOverride"/> takes, so a shape change from a
    /// future version costs the user one dry run and not a 500.
    /// </para>
    /// </summary>
    internal async Task<Results<Ok<ScanSummaryView>, NotFound, ForbiddenCode>> ScanLibraryResultAsync(
        ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (!HasAnyReadPermission(principal))
        {
            return new ForbiddenCode();
        }

        var json = await Store.GetAsync(LastScanSummaryKey, ct);
        if (string.IsNullOrEmpty(json))
        {
            return TypedResults.NotFound();
        }

        ScanSummary? summary;
        try
        {
            summary = JsonSerializer.Deserialize<ScanSummary>(json, PreviewResponseJsonOptions);
        }
        catch (JsonException)
        {
            return TypedResults.NotFound();
        }

        if (summary is null || summary.SchemaVersion != ScanSummary.CurrentSchemaVersion)
        {
            return TypedResults.NotFound();
        }

        var readableKinds = RenamableKinds.Where(k => principal.Current!.Has(PermissionsFor(k).Read)).ToArray();
        return TypedResults.Ok(ScanSummaryView.From(summary, readableKinds));
    }

    /// <summary>
    /// Serves one page of the whole-library dry run's rows, planned on demand through the same planner
    /// the scan job uses. Enforces ANY renamer-read permission in-handler (the host's
    /// <c>[RequiresPermission]</c> filter is inert on minimal-API routes, so this gate is the only one)
    /// and walks only the kinds the caller may read.
    /// </summary>
    /// <remarks>
    /// This is a LIVE preview, not a snapshot read: the page is planned with the options in the request
    /// (falling back to the saved options), so a caller sending the same blob it sent to
    /// <c>/scan-library</c> sees rows consistent with that scan without the scan having to store them.
    /// </remarks>
    /// <param name="body">Cursor, page size and filters; null means "the first page, unfiltered".</param>
    /// <param name="principal">The calling principal, gated and used to pick the readable kinds.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task<Results<Ok<ScanRowsPage>, BadRequest<ErrorCode>, ForbiddenCode>> ScanRowsAsync(
        ScanRowsRequest? body, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (!HasAnyReadPermission(principal))
        {
            return new ForbiddenCode();
        }

        if (!ScanBucket.TryParse(body?.Bucket, out var bucket))
        {
            return TypedResults.BadRequest(new ErrorCode("UNSUPPORTED_BUCKET"));
        }

        ScanCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(body?.Kind))
        {
            if (!TryParseKind(body.Kind, out var cursorKind))
            {
                return TypedResults.BadRequest(new ErrorCode("UNSUPPORTED_ENTITY_TYPE"));
            }

            cursor = new ScanCursor(cursorKind, Math.Max(body.AfterEntityId ?? 0, 0));
        }

        var options = TryParseOptionsOverride(body?.Options) ?? await new OptionsStore(Store, _log).LoadAsync(ct);
        var lookups = BuildLookups(options);
        var readableKinds = RenamableKinds.Where(k => principal.Current!.Has(PermissionsFor(k).Read)).ToArray();

        await using var scope = ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        var port = new CoveRenamerDataPort(db, _coveConfig);
        var pager = new ScanRowPager(new RenamerPlanner(port), port);

        var page = await pager.PageAsync(
            readableKinds, cursor, body?.Take ?? 0, body?.Query, bucket, options, lookups, ct);

        return TypedResults.Ok(page);
    }

    /// <summary>
    /// The whole-library scan job body: resolves the options, opens a scope for a live data port, and
    /// runs <see cref="RunScanCoreAsync"/> over it.
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
        var options = overrideOptions ?? await new OptionsStore(Store, _log).LoadAsync(ct);

        // The widest of the elevated bodies, because RunScanCoreAsync takes a PORT rather than a service
        // provider - deliberately, so its boundedness is provable over a fake - and so there is no
        // narrower seam here to wrap. Per-kind authorization still reaches the detached job through
        // readableKinds, captured at enqueue time.
        await RunAsSystem.RunInSystemScopeAsync(ScopeFactory, services =>
        {
            var db = services.GetRequiredService<DbContext>();
            return RunScanCoreAsync(new CoveRenamerDataPort(db, _coveConfig), readableKinds, options, progress, ct);
        });
    }

    /// <summary>
    /// The scan itself: for each readable kind, plans every entity through the SAME planner
    /// <see cref="PreviewAsync"/> uses and folds each plan into a <see cref="ScanAggregator"/>, then
    /// persists that bounded aggregate under <see cref="LastScanSummaryKey"/> in one write. ZERO
    /// disk/DB mutation — no <c>SaveAsync</c>, no <c>File.Move</c>.
    /// </summary>
    /// <remarks>
    /// Persists per-kind counts and blast radius, never the rows: a per-file collection here is
    /// O(library) in both the managed heap and the stored value, and one oversized stored value makes
    /// Cove's bulk extension-data read fail for every key this extension owns. The rows are served on
    /// demand instead, by the <c>/scan-rows</c> page query, through this same planner.
    /// <para>
    /// The port is a parameter rather than resolved here so the boundedness this method exists to
    /// guarantee can be proven over a fake port, with no live database.
    /// </para>
    /// </remarks>
    /// <param name="port">The read seam the scan plans through.</param>
    /// <param name="readableKinds">The kinds to scan, in the order they are given.</param>
    /// <param name="options">The options to plan with (saved or the caller's unsaved override).</param>
    /// <param name="progress">The job-progress sink reported a final <c>1.0</c> on completion.</param>
    /// <param name="ct">Cancellation token; a genuine cancellation aborts the scan.</param>
    internal async Task RunScanCoreAsync(
        IRenamerDataPort port, IReadOnlyList<RenamerFileKind> readableKinds, RenamerOptions options,
        Cove.Plugins.IJobProgress progress, CancellationToken ct)
    {
        var lookups = BuildLookups(options);
        var planner = new RenamerPlanner(port);
        var aggregator = new ScanAggregator(options.FullPathMax);

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
            await WriteScanSummaryAsync(aggregator, ct);
            LogScanDone(0, 0);
            progress.Report(1d, "Scan complete — nothing to scan.");
            return;
        }

        int done = 0;
        foreach (var (kind, ids) in idsByKind)
        {
            // The scan previously issued one heavy multi-Include query per entity (100K entities = 100K
            // sequential round-trips — the scan bottleneck). Batch-load instead, one chunk at a time:
            // the loaded entities AND their file-size map are released with each chunk, so neither the
            // graphs nor the sizes are ever held for the whole library. The chunk size is the port's own
            // single decision. Each chunk's entities are re-ordered by the (ascending) id list because
            // the batch load returns DB order, preserving the per-id order and the progress cadence.
            foreach (var chunk in ids.Chunk(CoveRenamerDataPort.LoadChunkSize))
            {
                var loaded = await port.LoadEntitiesAsync(kind, chunk, ct);
                var byId = loaded.ToDictionary(e => e.EntityId);
                var sizeByFileId = loaded
                    .SelectMany(e => e.Files)
                    .ToDictionary(f => f.FileId, f => f.SizeBytes);

                foreach (var id in chunk)
                {
                    ct.ThrowIfCancellationRequested();

                    // A missing entry means the id vanished between the id-list query and the batch load
                    // — it contributes nothing, matching the old path where PlanAsync on a missing id
                    // yielded an empty plan.
                    if (byId.TryGetValue(id, out var entity))
                    {
                        var plan = await planner.PlanLoadedEntity(entity, options, lookups, ct);
                        aggregator.Fold(kind, plan, sizeByFileId);
                    }

                    done++;
                    LogScanItemPlanned(done, total, kind, id);
                    // Report the fraction planned with a live message, capped just under 1.0 — the final
                    // 1.0 is reserved for after the result is persisted, so the UI only reads "complete"
                    // once the scan result is actually available to fetch.
                    progress.Report(Math.Min((double)done / total, 0.99), $"Scanning library… {done}/{total}");
                }
            }
        }

        await WriteScanSummaryAsync(aggregator, ct);

        LogScanDone(aggregator.TotalFiles, total);
        progress.Report(1d, "Scan complete.");
    }

    private Task WriteScanSummaryAsync(ScanAggregator aggregator, CancellationToken ct)
        => Store.SetAsync(
            LastScanSummaryKey,
            JsonSerializer.Serialize(aggregator.ToSummary(DateTime.UtcNow.Ticks), PreviewResponseJsonOptions),
            ct);

    /// <summary>
    /// Enqueues the whole-library renamer job. Takes NO request body and NO caller-supplied id array
    /// (same rationale as <see cref="ScanLibraryEnqueue"/>: <see cref="MaxEntityIdsPerRequest"/> does
    /// not apply). Coarse-gates on ANY renamer-write permission — 403 BEFORE any enqueue — then captures
    /// the principal's held write kinds into the job closure (the job runs detached from the request, so
    /// it cannot re-resolve <see cref="ICurrentPrincipalAccessor"/> itself).
    /// </summary>
    internal Results<Accepted<JobEnqueued>, ForbiddenCode> RenamerLibraryEnqueue(
        ICurrentPrincipalAccessor principal, IJobService jobs)
    {
        if (!HasAnyWritePermission(principal))
        {
            return new ForbiddenCode();
        }

        var writableKinds = RenamableKinds.Where(k => principal.Current!.Has(PermissionsFor(k).Write)).ToArray();

        var jobId = jobs.Enqueue(
            $"ext:{Id}:renamer-library",
            $"[{Name}] Renamer library",
            (coreProgress, ct) => RunRenamerLibraryJobAsync(writableKinds, new HostProgress(coreProgress), ct),
            exclusive: true);

        return TypedResults.Accepted((string?)null, new JobEnqueued(jobId));
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

            var ids = await RunAsSystem.RunInSystemScopeAsync(
                ScopeFactory,
                services =>
                {
                    var db = services.GetRequiredService<DbContext>();
                    return new CoveRenamerDataPort(db, _coveConfig).LoadAllEntityIdsAsync(kind, ct);
                });

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
    internal async Task<Results<Ok<IReadOnlyList<PreviewSampleResult>>, BadRequest<ErrorCode>, ForbiddenCode>> PreviewSampleAsync(
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
            return new ForbiddenCode();
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
                return TypedResults.BadRequest(new ErrorCode("INVALID_BODY"));
            }

            // Null Options (e.g. {"Options":null} or {}) → defaults; unknown JSON props ignored on parse.
            options = req?.Options;
        }

        options ??= new RenamerOptions();

        var results = SampleTokenSets.All
            .Select(sample => RenderSample(sample, options))
            .ToList();

        return TypedResults.Ok<IReadOnlyList<PreviewSampleResult>>(results);
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
