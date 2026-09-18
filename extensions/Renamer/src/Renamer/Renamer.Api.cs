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
/// <c>renamer-batch</c> job registration, and the minimal-API endpoints. Each job body lives with
/// the work it runs: <c>Renamer.Batch.cs</c> for the selected-item rename, <c>Renamer.Library.cs</c>
/// for the whole-library scan and rename, <c>Renamer.Undo.cs</c> for undo.
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
            // Both spellings, because the host's selection-action normalizer singularizes only "videos"
            // and "images": a texts list hands its extension actions the plural "texts". Declaring the
            // singular too keeps the action working if the host later normalizes every kind.
            .AddAction(
                id: "renamer-selected-text",
                label: "Rename selected",
                actionType: "bulk",
                entityTypes: ["text", "texts"],
                icon: "pencil",
                apiEndpoint: null,
                handlerName: "renamerSelected",
                order: 100,
                requiredPermission: Permissions.TextsWrite,
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
                description: "Build each filename from the item's own details. See every change before anything moves.",
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
            description: "Renames the items you selected, using your naming pattern.",
            supportsParameters: true,
            showInTaskList: true);

    /// <summary>
    /// Registers every endpoint, each DECLARING the coarse gate its own handler re-checks.
    /// </summary>
    /// <remarks>
    /// An endpoint carrying none of the SDK's authorization conventions is treated as anonymous for
    /// backward compatibility, and the host warns at boot naming every such route. The declaration is
    /// what the host reads and audits; the in-handler check stays, because it is what keeps behaviour
    /// identical on a host predating policy enforcement.
    /// <para>
    /// The gate is the any-of form, from the same <see cref="AnyReadPermissions"/> /
    /// <see cref="AnyWritePermissions"/> array the handler reads, so the two cannot drift.
    /// </para>
    /// <para>
    /// COARSE is the most the host can express here, and the handler keeps the rest. The precise
    /// per-kind re-checks have no endpoint-level equivalent: the kind travels in the request body and
    /// the host binds an entity policy to a ROUTE value only. Nor does a permission policy see role
    /// content rules - it admits any caller that HOLDS the permission - so a caller restricted to part
    /// of the library still reaches these routes, and only the handler narrows what it gets back.
    /// Refusing such a caller outright would need the check Cove applies to its own routes with
    /// <c>[RequiresUnscopedEntityAccess]</c>, an MVC action filter that never reaches a minimal-API
    /// endpoint.
    /// </para>
    /// <para>
    /// Every lambda delegates straight to an extracted instance method, so the logic is reachable
    /// without an HTTP host. The host resolves the lambda parameters from the request scope, and
    /// <c>ICurrentPrincipalAccessor</c> is populated by its CurrentPrincipalMiddleware.
    /// </para>
    /// </remarks>
    public override void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(PreviewRoute,
            (RenamerRequest req, DbContext db, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => PreviewAsync(req, db, principal, ct))
            .RequireCovePermission(PermissionMode.Any, AnyReadPermissions);

        endpoints.MapPost(RenamerRoute,
            (RenamerRequest req, ICurrentPrincipalAccessor principal, IJobService jobs)
                => RenamerEnqueue(req, principal, jobs))
            .RequireCovePermission(PermissionMode.Any, AnyWritePermissions);

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
            .RequireCovePermission(PermissionMode.Any, AnyReadPermissions);

        // /undo takes NO request body — it operates on "the last batch", so binding no body avoids
        // the host's enum-converter 400 trap (see the preview-sample note above); /last-batch is a plain read.
        endpoints.MapPost(UndoRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => UndoAsync(principal, ct))
            .RequireCovePermission(PermissionMode.Any, AnyWritePermissions);

        endpoints.MapGet(LastBatchRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => LastBatchAsync(principal, ct))
            .RequireCovePermission(PermissionMode.Any, AnyReadPermissions);

        endpoints.MapPost(ScanLibraryRoute,
            (ScanLibraryRequest? body, ICurrentPrincipalAccessor principal, IJobService jobs) =>
                ScanLibraryEnqueue(body, principal, jobs))
            .RequireCovePermission(PermissionMode.Any, AnyReadPermissions);

        endpoints.MapGet(LastScanRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => ScanLibraryResultAsync(principal, ct))
            .RequireCovePermission(PermissionMode.Any, AnyReadPermissions);

        endpoints.MapPost(ScanRowsRoute,
            (ScanRowsRequest? body, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => ScanRowsAsync(body, principal, ct))
            .RequireCovePermission(PermissionMode.Any, AnyReadPermissions);

        endpoints.MapPost(RenamerLibraryRoute,
            (ICurrentPrincipalAccessor principal, IJobService jobs) => RenamerLibraryEnqueue(principal, jobs))
            .RequireCovePermission(PermissionMode.Any, AnyWritePermissions);

        endpoints.MapGet(LibraryPathsRoute,
            (ICurrentPrincipalAccessor principal) => LibraryPaths(principal))
            .RequireCovePermission(PermissionMode.Any, AnyReadPermissions);

        endpoints.MapGet(JobStatusRoute,
            (string jobId, ICurrentPrincipalAccessor principal, IJobService jobs)
                => JobStatus(jobId, principal, jobs))
            .RequireCovePermission(PermissionMode.Any, AnyReadPermissions);

        endpoints.MapGet(OrphanedRulesRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct)
                => OrphanedRulesAsync(principal, ct))
            .RequireCovePermission(PermissionMode.Any, AnyReadPermissions);
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

        if (req.EntityIds is null)
        {
            return TypedResults.BadRequest(new ErrorCode("MISSING_ENTITY_IDS"));
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

        if (req.EntityIds is null)
        {
            return TypedResults.BadRequest(new ErrorCode("MISSING_ENTITY_IDS"));
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
    /// Returns the paths-free summary of the rename an undo would act on: its original file count, the
    /// moment it started, and its spent flag — no paths. The counts are totalled over every batch that
    /// rename opened, so a whole-library run reports what one press will put back. Enforces <c>videos.read</c>
    /// in-handler (403-first; minimal-API <c>[RequiresPermission]</c> is inert). An empty journal
    /// returns <see cref="LastBatchSummary"/> with <c>HasBatch:false</c>.
    /// </summary>
    /// <remarks>
    /// It is spent when no row is left to restore, which is derived from the aggregate rather
    /// than stored: a row exists exactly while its file still needs restoring, so "nothing remains" and
    /// "already undone" are the same fact and cannot disagree.
    /// <para>
    /// Reads the batch rows only, one aggregate per rename. It never pages the row table, so the
    /// response stays fixed whatever the rename's size — which is also what keeps this endpoint's
    /// coarse permission gate defensible.
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
        // describe the work the button will do. Two reads that merely agreed today drifted the moment a
        // newer batch could settle while an older one still held rows.
        //
        // The counts are the operation's, summed over every batch the click opened, and the timestamp
        // is the earliest of them: the moment the user clicked, not the moment its last kind started.
        var summary = await journal.ReadUndoTargetAsync(ct);
        return TypedResults.Ok(new LastBatchSummary(
            HasBatch: summary is not null,
            Count: summary?.OriginalCount ?? 0,
            RemainingCount: summary?.Remaining ?? 0,
            UnrestorableCount: summary?.UnrestorableCount ?? 0,
            WrittenAtUtcTicks: summary?.OpenedAtUtcTicks ?? 0,
            Consumed: summary is not null && summary.Value.Remaining == 0));
    }

    /// <summary>The read gate every renamer route declares, and the one its handler re-checks.</summary>
    /// <remarks>
    /// ONE array, read by both, because the divergence is what would go unnoticed: an endpoint
    /// advertising one gate to the host while enforcing another still passes every test that drives the
    /// handler directly.
    /// </remarks>
    private static readonly string[] AnyReadPermissions =
        [.. RenamableKinds.All.Select(k => PermissionsFor(k).Read)];

    /// <summary>The write gate, on the same terms as <see cref="AnyReadPermissions"/>.</summary>
    private static readonly string[] AnyWritePermissions =
        [.. RenamableKinds.All.Select(k => PermissionsFor(k).Write)];

    private static bool HasAnyReadPermission(ICurrentPrincipalAccessor principal)
        => principal.Current is { } current && Array.Exists(AnyReadPermissions, current.Has);

    private static bool HasAnyWritePermission(ICurrentPrincipalAccessor principal)
        => principal.Current is { } current && Array.Exists(AnyWritePermissions, current.Has);

    /// <summary>
    /// Enqueues the whole-library scan job. Takes an OPTIONAL <see cref="ScanLibraryRequest"/> body
    /// carrying the caller's current options (for a dry run on unsaved edits); with no body it scans the
    /// saved options. Takes NO caller-supplied id array — the candidate ids are server-derived per kind
    /// via <see cref="IRenamerDataPort.LoadEntityIdPageAsync"/> inside the job, so
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

        var readableKinds = RenamableKinds.All.Where(k => principal.Current!.Has(PermissionsFor(k).Read)).ToArray();

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

        var readableKinds = RenamableKinds.All.Where(k => principal.Current!.Has(PermissionsFor(k).Read)).ToArray();
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
        // A kind turned off is dropped before the walk, exactly as RunScanCoreAsync drops it. Left in,
        // a library-sized kind that is off fills the table with rows saying so and spends the request's
        // entity budget reaching them, while the counts beside that table exclude it, and the table and its
        // own summary would disagree. A cursor minted while the kind was on resumes at the next kind.
        var kinds = RenamableKinds.All
            .Where(k => principal.Current!.Has(PermissionsFor(k).Read) && options.IsKindEnabled(k))
            .ToArray();

        await using var scope = ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        var port = new CoveRenamerDataPort(db, _coveConfig);
        var pager = new ScanRowPager(new RenamerPlanner(port), port);

        var page = await pager.PageAsync(
            kinds, cursor, body?.Take ?? 0, body?.Query, bucket, options, lookups, ct);

        return TypedResults.Ok(page);
    }

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

        var writableKinds = RenamableKinds.All.Where(k => principal.Current!.Has(PermissionsFor(k).Write)).ToArray();

        var jobId = jobs.Enqueue(
            $"ext:{Id}:renamer-library",
            $"[{Name}] Renamer library",
            (coreProgress, ct) => RunRenamerLibraryJobAsync(writableKinds, new HostProgress(coreProgress), ct),
            exclusive: true);

        return TypedResults.Accepted((string?)null, new JobEnqueued(jobId));
    }

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
