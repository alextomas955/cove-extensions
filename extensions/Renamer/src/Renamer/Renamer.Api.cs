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
/// The Cove-facing surface of the extension: the "Rename selected" bulk action, the job
/// registrations, and the minimal-API endpoints. Each job body lives with the work it runs, in
/// <c>Renamer.Batch.cs</c>, <c>Renamer.Library.cs</c> and <c>Renamer.Undo.cs</c>.
/// </summary>
public sealed partial class Renamer
{
    // The action's endpoint reference and the mapped route have to be the same literal, so derive
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
    private string OptionsRoute => RouteBase + "/options";

    // An early scan wrote one wire row per file to this key. Retained only so InitializeAsync can
    // delete it; nothing reads it.
    internal const string LastScanResultKey = "last-scan-result";

    // The fixed store key the whole-library scan's bounded aggregate lives under.
    internal const string LastScanSummaryKey = "last-scan-summary";

    // Upper bound on how many ids a single preview/renamer request may carry. Preview runs the planner
    // (DB hits) per id synchronously on the request thread, and renamer fans the same ids out into one
    // job — so a caller-supplied array is an unbounded fan-out. The cap rejects a runaway/oversized
    // request up front with a 400, before any per-id work, while staying far above any realistic
    // selection. A genuinely larger job should be split into batches by the caller.
    private const int MaxEntityIdsPerRequest = 1000;

    // The "Rename selected" bulk action, registered once per entity kind so each carries its own
    // RequiredPermission. The host allows one RequiredPermission per action and filters visibility by
    // both entity-type context and that permission, so a single video-and-image action gated on
    // videos.write would hide the button from an images-only writer viewing images. Audio stays
    // reachable through the job and API but is not surfaced as a button.
    //
    // Each action declares a handler name and no ApiEndpoint, so the host dispatches the JS handler
    // the bundle registers. That is what lets the handler preview and confirm before anything
    // touches disk. RequiredPermission is a UI affordance; the endpoints re-check the request kind's
    // permission server-side.
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
            // The renamer UI is a settings page, not a stack of section cards: it is a configurator
            // with a template editor, live preview, whole-library run and undo. Page layout makes the
            // host render the panel full-width with no card chrome, and this extension owns the
            // canvas. A page sources its content from the panels targeting it exactly as the default
            // layout does, so "RenamerPage" is contributed as a section, and its componentName has to
            // equal the key in the bundle's defineExtension components map. Needs the host's
            // page-layout settings support; see minCoveVersion.
            .AddSettingsTab(
                key: "renamer",
                label: "Renamer",
                description: "Build each filename from the item's own details. See every change before anything moves.",
                order: 100,
                layout: SettingsTabLayout.Page)
            .AddSettingsSection(targetTab: "renamer", label: "Renamer", componentName: "RenamerPage")
            .WithJsBundle("index.mjs")
            .Build();

    // Invoked from the FullExtensionBase constructor, so RenamerJob.JobId must already exist here.
    protected override void DefineJobs()
        => Job(
            id: RenamerJob.JobId,
            name: "Rename selected",
            handler: (parameters, progress, ct) => RunRenamerBatchAsync(parameters, progress, ct),
            description: "Renames the items you selected, using your naming pattern.",
            supportsParameters: true,
            showInTaskList: true);

    // Each endpoint declares the coarse gate its own handler re-checks. An endpoint carrying none of
    // the SDK's authorization conventions is treated as anonymous, and the host warns at boot naming
    // every such route. The in-handler check stays, because it keeps behavior identical on a host
    // predating policy enforcement. Both read the same AnyReadPermissions and AnyWritePermissions
    // arrays, so the declaration and the enforcement cannot drift.
    //
    // Coarse is the most the host can express here. A per-kind check has no endpoint-level
    // equivalent: the kind travels in the request body and the host binds an entity policy to a
    // route value only. A permission policy also admits any caller holding the permission, without
    // seeing role content rules, so a caller restricted to part of the library still reaches these
    // routes and only the handler narrows what comes back.
    //
    // Every lambda delegates to an extracted instance method, so the logic is reachable without an
    // HTTP host.
    public override void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(PreviewRoute,
            (RenamerRequest req, DbContext db, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => PreviewAsync(req, db, principal, ct))
            .RequireCovePermission(PermissionMode.Any, AnyReadPermissions);

        endpoints.MapPost(RenamerRoute,
            (RenamerRequest req, ICurrentPrincipalAccessor principal, IJobService jobs,
                IAuthorizationService authz, CancellationToken ct)
                => RenamerEnqueue(req, principal, jobs, authz, ct))
            .RequireCovePermission(PermissionMode.Any, AnyWritePermissions);

        // NB: this endpoint binds the raw HttpContext (not a typed PreviewSampleRequest) so the
        // handler can deserialize the body with RenamerOptions.JsonOptions — the host's default
        // minimal-API JsonSerializerOptions has no JsonStringEnumConverter, so a body carrying
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

        // /undo takes no request body — it operates on "the last batch", so binding no body avoids
        // the host's enum-converter 400 trap (see the preview-sample note above); /last-batch is a plain read.
        endpoints.MapPost(UndoRoute,
            (ICurrentPrincipalAccessor principal, IAuthorizationService authz, CancellationToken ct)
                => UndoAsync(principal, authz, ct))
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

        // Gated on extensions.configure, not on a media permission: one settings document decides how
        // every kind is named and where it is moved, and the auto-rename it can switch on runs later as
        // System. Holding write over one kind is not consent to reconfigure the extension. This is the
        // permission Cove's own extension-data routes carry, which is where these settings lived.
        endpoints.MapGet(OptionsRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => GetOptionsAsync(principal, ct))
            .RequireCovePermission(Permissions.ExtensionsConfigure);

        // Binds the raw HttpContext for the reason /preview-sample does: the body carries string enum
        // values, and the host's minimal-API serializer has no enum converter, so typed binding would
        // 400 before the handler ran. .Accepts<> is what puts the request schema in the document.
        endpoints.MapPut(OptionsRoute,
            (HttpContext http, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => SaveOptionsAsync(http.Request, principal, ct))
            .Accepts<RenamerOptions>("application/json")
            .RequireCovePermission(Permissions.ExtensionsConfigure);
    }

    // The saved settings the panel edits, read through the same store every job reads them through, so
    // the panel and a rename can never disagree about what a stored blob means.
    internal async Task<Results<Ok<OptionsView>, ForbiddenCode>> GetOptionsAsync(
        ICurrentPrincipalAccessor principal, CancellationToken ct = default)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var stored = await Store.GetAsync(OptionsStore.Key, ct);
        var options = await new OptionsStore(Store, _log).LoadAsync(ct);

        return TypedResults.Ok(new OptionsView(
            options,
            PendingNameMigration: OptionsMigration.Scan(stored).Any,
            PendingDestinationMigration: OptionsMigration.HasLegacyDestinations(stored),
            Unreadable: !string.IsNullOrWhiteSpace(stored) && TryParseOptionsOverride(stored) is null));
    }

    // Persists the settings, in the spelling the store owns rather than the wire's.
    //
    // The stored document is replaced, not merged: what goes back is the members RenamerOptions
    // declares. A property only a newer version knows about is readable here, because a load ignores
    // what it does not recognize, but it does not survive this write.
    //
    // Refused while the stored blob still holds a shape the one-time conversion has not resolved: the
    // model binds a name-keyed rule to nothing and a bare destination path to the destination that moves
    // nothing, so a save would write those blanks over the only copy of the user's rules. The conversion
    // defers until Cove has supplied the entity rows or the library paths it needs, so this outlives a
    // restart and is not a race.
    internal async Task<Results<NoContent, BadRequest<ErrorCode>, Conflict<ErrorCode>, ForbiddenCode>> SaveOptionsAsync(
        HttpRequest request, ICurrentPrincipalAccessor principal, CancellationToken ct = default)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var stored = await Store.GetAsync(OptionsStore.Key, ct);
        if (OptionsMigration.Scan(stored).Any || OptionsMigration.HasLegacyDestinations(stored))
        {
            return TypedResults.Conflict(new ErrorCode("MIGRATION_PENDING"));
        }

        RenamerOptions? options;
        try
        {
            options = await JsonSerializer.DeserializeAsync<RenamerOptions>(
                request.Body, RenamerOptions.JsonOptions, ct);
        }
        catch (JsonException)
        {
            return TypedResults.BadRequest(new ErrorCode("INVALID_OPTIONS"));
        }

        if (options is null)
        {
            return TypedResults.BadRequest(new ErrorCode("INVALID_OPTIONS"));
        }

        await new OptionsStore(Store, _log).SaveAsync(options, ct);
        return TypedResults.NoContent();
    }

    // Cove's configured library paths: the list every destination root is chosen from, so the
    // settings panel offers exactly the roots the planner accepts and no typed path can drift from
    // them. Reads in-memory host settings, so it opens no scope and touches no database. Gated on any
    // renamer-read permission: the list says where a rename may write, not what the library holds.
    internal Results<Ok<LibraryPathsView>, ForbiddenCode> LibraryPaths(
        ICurrentPrincipalAccessor principal)
        => HasAnyReadPermission(principal)
            ? TypedResults.Ok(new LibraryPathsView(LibraryRoots))
            : new ForbiddenCode();

    // The prefix the host mints onto every job type this extension enqueues.
    private string OwnJobTypePrefix => "ext:" + Id + ":";

    // Where one of this extension's own runs has got to. Cove gates its job route on unrestricted
    // read, so a scoped account is refused there even for a run it started, and this serves the same
    // few fields under the extension's own check.
    //
    // A job whose type does not carry OwnJobTypePrefix is reported as not found. Answering forbidden
    // would confirm that id names a real job, which is the fact the host's gate withholds.
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

    // Which per-studio and per-tag rule keys name an entity Cove no longer holds. Read as System,
    // because the question is whether the entity exists and a caller who cannot see it would
    // otherwise be told it is gone. Only the caller's own unresolved rule keys are returned, so
    // elevating discloses no more than the rules they already hold. The query asks about exactly the
    // ids the rules name, so it is bounded by rule count, and no rules means no query.
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

    // The synchronous read-only dry run: plans each requested id and returns the accumulated items.
    // Mutates nothing. The permission is enforced in-handler because the host's filter is MVC-only
    // and inert on minimal-API endpoints.
    internal async Task<Results<Ok<PreviewResponse>, BadRequest<ErrorCode>, ForbiddenCode>> PreviewAsync(
        RenamerRequest req, DbContext db, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        // Resolve the kind first so the permission check below gates on the request's own entity kind
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

        // Build the RouteLookups the batch builds and route through the routing overload,
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
            // file's bytes by id; the aggregate reads them per acting item. Mirrors the batch's planning pass.
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
        // budget is read from the loaded options once and handed to both halves of the response below, so
        // the aggregate's in-flight overflow count and the per-item flags cannot be measured against
        // different limits and disagree.
        var summary = BatchPreview.Summarize(items, sizeByFileId, options.FullPathMax);

        // The host's serializer is camelCase but emits NUMERIC enums (status:0), which the frontend's
        // buildConfirmSummary reads as a non-renamer — so the renamer would silently never fire. The
        // string spelling comes from CamelCaseStringEnumConverter declared on RenamerStatus and
        // ConfirmLevel, never from an options instance chosen here.
        return TypedResults.Ok(
            new PreviewResponse(
                [.. items.Select(i => PreviewItemView.From(
                    i, BatchPreview.InFlightPathOverflows(i, options.FullPathMax)))],
                summary));
    }

    // Encodes the request into the job parameters and hands the host a delegate that calls
    // RunRenamerBatchAsync. Returns 403 before any enqueue.
    //
    // One id the caller cannot write refuses the whole request, and the 403 carries no body, so the
    // response names none of the ids that were denied. The per-entity decision runs in the request
    // scope, where the caller's principal is live, so it needs no snapshot.
    internal async Task<Results<Accepted<JobEnqueued>, BadRequest<ErrorCode>, ForbiddenCode>> RenamerEnqueue(
        RenamerRequest req, ICurrentPrincipalAccessor principal, IJobService jobs,
        IAuthorizationService authz, CancellationToken ct)
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

        var allowed = await EntityAccessGuard.AllowedOnlyAsync(
            authz, principal.Current, kind, writePermission, req.EntityIds, ct);
        if (allowed.Count != req.EntityIds.Length)
        {
            return new ForbiddenCode();
        }

        var parameters = RenamerJob.Encode(req.EntityType, req.EntityIds);

        // Enqueue exclusive (the host's JobService default): a renamer batch mutates disk + DB, so two
        // batches running at once could plan against each other's stale snapshots or target the same
        // paths. Exclusive serializes them — the second waits for the first to finish.
        var jobId = jobs.Enqueue(
            $"ext:{Id}:{RenamerJob.JobId}",
            $"[{Name}] Rename selected",
            (coreProgress, ct) => RunRenamerBatchAsync(parameters, new HostProgress(coreProgress), ct),
            exclusive: true);

        return TypedResults.Accepted((string?)null, new JobEnqueued(jobId));
    }

    // The summary of the rename an undo would act on: original file count, start moment and spent
    // flag, and no paths. Counts are totalled over every batch that rename opened, so a
    // whole-library run reports what one press puts back.
    //
    // Spent is derived from the aggregate, not stored: a row exists exactly while its file still
    // needs restoring, so "nothing remains" and "already undone" are one fact and cannot disagree.
    // Reads the batch rows only and never pages the row table, so the response size is fixed
    // whatever the rename's size.
    internal async Task<Results<Ok<LastBatchSummary>, ForbiddenCode>> LastBatchAsync(
        ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        // This is the undo panel's paths-free "is there a batch to undo?" probe (count + timestamp +
        // consumed flag only — no paths). A user who can renamer any kind may see it, so gate on holding
        // any renamer-read permission rather than videos.read specifically. The summary does not carry
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

        await using var journal = new CoveRevertJournal(db);

        // The read /undo names its target with, which is what makes the line this endpoint feeds
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

    // One array, read by the route declaration and by the handler. An endpoint advertising one gate
    // to the host while enforcing another still passes every test that drives the handler directly.
    private static readonly string[] AnyReadPermissions =
        [.. RenamableKinds.All.Select(k => PermissionsFor(k).Read)];

    // The write gate, on the same terms.
    private static readonly string[] AnyWritePermissions =
        [.. RenamableKinds.All.Select(k => PermissionsFor(k).Write)];

    private static bool HasAnyReadPermission(ICurrentPrincipalAccessor principal)
        => principal.Current is { } current && Array.Exists(AnyReadPermissions, current.Has);

    private static bool HasAnyWritePermission(ICurrentPrincipalAccessor principal)
        => principal.Current is { } current && Array.Exists(AnyWritePermissions, current.Has);

    // Enqueues the whole-library scan. The optional body carries the caller's current options for a
    // dry run on unsaved edits; with no body it scans the saved options. It takes no caller-supplied
    // id array, since the candidate ids are server-derived per kind inside the job, so
    // MaxEntityIdsPerRequest has nothing to bound here.
    //
    // Returns 403 before any enqueue, then captures the principal's held read kinds into the job
    // closure: the detached job cannot re-resolve the principal, and this is how it applies the same
    // per-kind skip a partial-permission caller sees from a preview. A copy of the principal itself
    // is captured beside them, because holding a kind's read permission does not grant read access to
    // every entity of that kind and the job authorizes each candidate it derives. The summary is
    // persisted under a fixed key, because the id Enqueue mints is not available to the job body
    // before Enqueue returns.
    internal Results<Accepted<JobEnqueued>, ForbiddenCode> ScanLibraryEnqueue(
        ScanLibraryRequest? body, ICurrentPrincipalAccessor principal, IJobService jobs)
    {
        if (!HasAnyReadPermission(principal))
        {
            return new ForbiddenCode();
        }

        // Dry-run-on-unsaved-edits: when the caller sends its current options blob, parse it with the
        // The tolerant options set OptionsStore uses, so the scan interprets it identically to a saved
        // load; a null/blank/corrupt blob falls back to the persisted options (the original no-body
        // behavior). Parsed here at enqueue time, then captured into the detached job closure — the job
        // cannot re-read the request, exactly like readableKinds.
        var overrideOptions = TryParseOptionsOverride(body?.Options);

        var readableKinds = RenamableKinds.All.Where(k => principal.Current!.Has(PermissionsFor(k).Read)).ToArray();
        var caller = EntityAccessGuard.Snapshot(principal.Current);

        var jobId = jobs.Enqueue(
            $"ext:{Id}:scan-library",
            $"[{Name}] Scan library",
            (coreProgress, ct) => RunScanLibraryJobAsync(caller, readableKinds, overrideOptions, new HostProgress(coreProgress), ct),
            exclusive: true);

        return TypedResults.Accepted((string?)null, new JobEnqueued(jobId));
    }

    // Returns null when the blob is absent, blank or unparseable, so a corrupt override falls back
    // to the saved options and does not fail the scan. Matches OptionsStore's tolerant read.
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

    // Reads back the last completed scan's aggregate, or 404 when none has completed. There is no
    // per-jobId tracking, so a 404 also covers an unknown job id; the panel only asks whether the
    // scan it started is done.
    //
    // The aggregate is written under a fixed key by whoever last ran the scan, capturing their
    // readable kinds, so a higher-permission scan can hold figures a video-only reader may not see.
    // The merge keeps only the kinds the current caller can read.
    //
    // A blob that will not parse, or one stamped with an unrecognised schema version, reads as "no
    // scan yet", so a future shape change costs the user one dry run and not a 500.
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

    // One page of the whole-library dry run's rows, planned on demand through the planner the scan
    // job uses, over only the kinds the caller may read. A null body means the first page,
    // unfiltered.
    //
    // A live preview, not a snapshot read: the page is planned with the options in the request,
    // falling back to the saved ones, so a caller sending the blob it sent to /scan-library sees rows
    // consistent with that scan without the scan storing them.
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

    // Enqueues the whole-library rename. No body and no caller-supplied id array, on the same terms
    // as the scan. Returns 403 before any enqueue, then captures the principal's held write kinds
    // into the job closure, because the detached job cannot re-resolve the principal. A copy of the
    // principal is captured beside them, because holding a kind's write permission does not grant
    // write access to every entity of that kind and the job authorizes each candidate it derives.
    internal Results<Accepted<JobEnqueued>, ForbiddenCode> RenamerLibraryEnqueue(
        ICurrentPrincipalAccessor principal, IJobService jobs)
    {
        if (!HasAnyWritePermission(principal))
        {
            return new ForbiddenCode();
        }

        var writableKinds = RenamableKinds.All.Where(k => principal.Current!.Has(PermissionsFor(k).Write)).ToArray();
        var caller = EntityAccessGuard.Snapshot(principal.Current);

        var jobId = jobs.Enqueue(
            $"ext:{Id}:renamer-library",
            $"[{Name}] Renamer library",
            (coreProgress, ct) => RunRenamerLibraryJobAsync(caller, writableKinds, new HostProgress(coreProgress), ct),
            exclusive: true);

        return TypedResults.Accepted((string?)null, new JobEnqueued(jobId));
    }

    // Runs the template engine over fixed samples with the in-flight options from the request body.
    // Selection-less and pure: no planner, no database, no disk, so a hostile template cannot escape
    // or amplify. The permission is enforced before any body read or engine work.
    //
    // The body is deserialized with RenamerOptions.JsonOptions, not the host's default minimal-API
    // options, which carry no enum converter and would 400 a panel body holding string enum values.
    // An empty or null-options body takes the defaults; malformed JSON is a 400.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Kept as an instance method to match its sibling endpoint handlers " +
            "(PreviewAsync/RenamerEnqueue/UndoAsync/LastBatchAsync) and the test call sites that invoke " +
            "it through an extension instance; making it static would churn those call sites without " +
            "any behavior change.")]
    internal async Task<Results<Ok<IReadOnlyList<PreviewSampleResult>>, BadRequest<ErrorCode>, ForbiddenCode>> PreviewSampleAsync(
        HttpRequest httpReq, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        // Enforce permission before touching the body — never read/parse for an unauthorized caller.
        // The sample preview is a pure template render over fixed Video/Image/Audio samples (no DB, no
        // selection), so gate on holding any renamer-read permission rather than videos.read specifically.
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

    // Renders one sample and derives its advisory flags: empty when the rendered name has no name
    // component, sanitized when the sanitize step changed it, length-reduced when the reducer dropped
    // fields, and gating-skip when a required-field token resolves empty. The dropped field names
    // come from the engine, never a string diff.
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
