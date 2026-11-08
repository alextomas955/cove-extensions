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
    // The route prefix mirrors how the host mounts an extension's IApiExtension endpoints:
    // /api/extensions/{id}/…
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
    private string LastScanRoute => RouteBase + "/last-scan/{runId}";
    private string LastLibraryRenameRoute => RouteBase + "/last-library-rename/{runId}";
    private string ScanRowsRoute => RouteBase + "/scan-rows";
    private string RenamerLibraryRoute => RouteBase + "/renamer-library";
    private string LibraryPathsRoute => RouteBase + "/library-paths";
    private string JobStatusRoute => RouteBase + "/job-status/{jobId}";
    private string OrphanedRulesRoute => RouteBase + "/orphaned-rules";
    private string OptionsRoute => RouteBase + "/options";

    // An early scan wrote one wire row per file to this key. Retained only so InitializeAsync can
    // delete it; nothing reads it.
    internal const string LastScanResultKey = "last-scan-result";

    // The fixed store key the whole-library scan's bounded aggregate lives under. One value, the
    // latest scan's, carrying the run id and owner key the readback matches on.
    internal const string LastScanSummaryKey = "last-scan-summary";

    // The fixed store key the last whole-library rename's per-kind counts live under, on the same terms.
    internal const string LastLibraryRenameSummaryKey = "last-library-rename-summary";

    // Upper bound on how many ids a single /preview or /renamer request may carry. Preview plans each
    // id on the request thread and /renamer fans the same ids into one job, so a caller-supplied array
    // is an unbounded fan-out; an oversized one is refused with a 400 before any per-id work.
    private const int MaxEntityIdsPerRequest = 1000;

    // The "Rename selected" bulk action, registered once per entity kind so each carries its own
    // RequiredPermission. The host allows one RequiredPermission per action and filters visibility by
    // both entity-type context and that permission, so a single video-and-image action gated on
    // videos.write would hide the button from an images-only writer viewing images. Audio stays
    // reachable through the job and API but is not surfaced as a button.
    //
    // Each action declares a handler name and no ApiEndpoint, so the host dispatches the JS handler
    // the bundle registers, which previews and confirms before anything touches disk.
    // RequiredPermission is a UI affordance; the endpoints re-check the request kind's permission
    // server-side.
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
                // The rename reports into the host's job drawer, so its queued-success alert is
                // suppressed. The before-disk confirm gate stays.
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
            // and "images": a texts list hands its extension actions the plural "texts".
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
            // Page layout makes the host render the panel full-width with no card chrome. A page
            // sources its content from the panels targeting it, so "RenamerPage" is contributed as a
            // section, and its componentName has to equal the key in the bundle's defineExtension
            // components map. Needs the host's page-layout settings support; see minCoveVersion.
            .AddSettingsTab(
                key: "renamer",
                label: "Renamer",
                description: "Build a filename from each item's metadata. Preview before anything touches disk.",
                order: 100,
                layout: SettingsTabLayout.Page)
            .AddSettingsSection(targetTab: "renamer", label: "Renamer", componentName: "RenamerPage")
            .WithJsBundle("index.mjs")
            .Build();

    // Each endpoint declares the coarse gate its own handler re-checks. An endpoint carrying none of
    // the SDK's authorization conventions is treated as anonymous, and the host warns at boot naming
    // every such route. The declaration and the handler read the same AnyReadPermissions and
    // AnyWritePermissions arrays, so they cannot drift.
    //
    // Coarse is the most the host can express here. A per-kind check has no endpoint-level
    // equivalent: the kind travels in the request body and the host binds an entity policy to a
    // route value only. A permission policy also admits any caller holding the permission, without
    // seeing role content rules, so a caller restricted to part of the library still reaches these
    // routes and only the handler narrows what comes back.
    //
    // Every route carries a WithName, which is both the ASP.NET endpoint name and the operationId in
    // the emitted document. Endpoint names are case-sensitive and must be unique across the whole
    // host process, which every installed extension shares, so each name is prefixed with this
    // extension's own. Without a name the generator falls back to the method and path, which spells
    // a type RenamerGetOptions as GetApiExtensionsComAlextomas955RenamerOptions and renames it
    // whenever a route moves. A name is therefore part of the published contract: changing one
    // renames a symbol in the generated client.
    public override void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(PreviewRoute,
            (RenamerRequest req, DbContext db, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => PreviewAsync(req, db, principal, ct))
            .RequireCovePermission(PermissionMode.Any, AnyReadPermissions)
            .WithName("RenamerPreview")
            .WithSummary("Preview the rename of a named selection")
            .WithDescription(
                "Plans every file of the given entities against the saved settings and returns the planned move for each, plus the totals a confirmation needs. Touches no disk.");

        endpoints.MapPost(RenamerRoute,
            (RenamerRequest req, ICurrentPrincipalAccessor principal, IJobService jobs,
                IAuthorizationService authz, CancellationToken ct)
                => RenamerEnqueue(req, principal, jobs, authz, ct))
            .RequireCovePermission(PermissionMode.Any, AnyWritePermissions)
            .WithName("RenamerRenameSelected")
            .WithSummary("Rename a named selection")
            .WithDescription(
                "Enqueues the rename of the given entities and returns at once with the job to poll. Each entity is authorized against the caller as the job runs, so a job may rename fewer than were asked for.");

        // The handler reads the raw request so an empty body means the defaults, which typed binding
        // cannot express. No parameter declares the body, so .Accepts<> is what puts its schema in the
        // emitted document and under its drift check.
        endpoints.MapPost(PreviewSampleRoute,
            (HttpContext http, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => PreviewSampleAsync(http.Request, principal, ct))
            .Accepts<PreviewSampleRequest>("application/json")
            .RequireCovePermission(PermissionMode.Any, AnyReadPermissions)
            .WithName("RenamerPreviewSample")
            .WithSummary("Preview sample names for unsaved settings")
            .WithDescription(
                "Renders the sample set against settings that have not been saved, which is what the settings page shows as the user types. An empty body, or one naming no settings, previews the defaults rather than what is stored.");

        // /undo takes no request body: it operates on "the last batch", and binding no body avoids
        // the host's enum-converter 400 trap.
        endpoints.MapPost(UndoRoute,
            (ICurrentPrincipalAccessor principal, IAuthorizationService authz, CancellationToken ct)
                => UndoAsync(principal, authz, ct))
            .RequireCovePermission(PermissionMode.Any, AnyWritePermissions)
            .WithName("RenamerUndo")
            .WithSummary("Undo the last rename batch")
            .WithDescription(
                "Moves the files of the most recent batch back to the names they had. Every arm it answers carries counts, including the one where there was nothing to undo, so a caller never has to infer what happened. A caller the permission gate refuses is the exception: that answers 403 with no counts.");

        endpoints.MapGet(LastBatchRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => LastBatchAsync(principal, ct))
            .RequireCovePermission(PermissionMode.Any, AnyReadPermissions)
            .WithName("RenamerGetLastBatch")
            .WithSummary("Summarize the last rename batch")
            .WithDescription(
                "What an undo would act on, and whether it still can: a batch expires, and one already undone reports as consumed.");

        endpoints.MapPost(ScanLibraryRoute,
            (ScanLibraryRequest? body, ICurrentPrincipalAccessor principal, IJobService jobs) =>
                ScanLibraryEnqueue(body, principal, jobs))
            .RequireCovePermission(PermissionMode.Any, AnyReadPermissions)
            .WithName("RenamerScanLibrary")
            .WithSummary("Scan the whole library for planned renames")
            .WithDescription(
                "Enqueues the scan and returns the job to poll along with the run its rows are read under.");

        endpoints.MapGet(LastScanRoute,
            (string runId, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => ScanLibraryResultAsync(runId, principal, ct))
            .RequireCovePermission(PermissionMode.Any, AnyReadPermissions)
            .WithName("RenamerGetScanResult")
            .WithSummary("Read a finished scan's totals")
            .WithDescription(
                "Only the most recent run's figures are kept, so a run replaced by a later scan answers 404.");

        endpoints.MapPost(ScanRowsRoute,
            (ScanRowsRequest? body, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => ScanRowsAsync(body, principal, ct))
            .RequireCovePermission(PermissionMode.Any, AnyReadPermissions)
            .WithName("RenamerGetScanRows")
            .WithSummary("Read a page of a scan's planned renames")
            .WithDescription(
                "A POST because the cursor, the search and the settings the scan ran under travel in the body. A page carries the cursor the next one resumes from; a page without one ends the walk. A page may also stop on the server's per-request budget with the library only partly examined, which is not the same as having no more results.");

        endpoints.MapGet(LastLibraryRenameRoute,
            (string runId, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => LibraryRenameResultAsync(runId, principal, ct))
            .RequireCovePermission(PermissionMode.Any, AnyReadPermissions)
            .WithName("RenamerGetLibraryRenameResult")
            .WithSummary("Read a finished library rename's counts")
            .WithDescription(
                "Only the most recent run's counts are kept, so a run replaced by a later rename answers 404.");

        endpoints.MapPost(RenamerLibraryRoute,
            (ICurrentPrincipalAccessor principal, IJobService jobs) => RenamerLibraryEnqueue(principal, jobs))
            .RequireCovePermission(PermissionMode.Any, AnyWritePermissions)
            .WithName("RenamerRenameLibrary")
            .WithSummary("Rename the whole library")
            .WithDescription(
                "Enqueues the rename of every entity the caller may write and returns the job to poll.");

        endpoints.MapGet(LibraryPathsRoute,
            (ICurrentPrincipalAccessor principal) => LibraryPaths(principal))
            .RequireCovePermission(PermissionMode.Any, AnyReadPermissions)
            .WithName("RenamerGetLibraryPaths")
            .WithSummary("List the library paths Cove is configured with")
            .WithDescription(
                "The roots a destination rule may send files to. Host configuration, not library data.");

        endpoints.MapGet(JobStatusRoute,
            (string jobId, ICurrentPrincipalAccessor principal, IJobService jobs)
                => JobStatus(jobId, principal, jobs))
            .RequireCovePermission(PermissionMode.Any, AnyReadPermissions)
            .WithName("RenamerGetJobStatus")
            .WithSummary("Read the status of a job this extension started")
            .WithDescription(
                "Scoped to the caller who started it. The extension answers this itself because the host's own job route requires unrestricted read, which a scoped account does not have.");

        endpoints.MapGet(OrphanedRulesRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct)
                => OrphanedRulesAsync(principal, ct))
            .RequireCovePermission(PermissionMode.Any, AnyReadPermissions)
            .WithName("RenamerGetOrphanedRules")
            .WithSummary("List routing rules whose entity no longer exists")
            .WithDescription(
                "Answered by the server because a browser cannot tell a deleted entity from one this viewer may not read.");

        // Gated on extensions.configure, not on a media permission: one settings document decides how
        // every kind is named and where it is moved, and the auto-rename it can switch on runs later as
        // System. Holding write over one kind is not consent to reconfigure the extension. This is the
        // permission Cove's own extension-data routes carry, which is where these settings lived.
        endpoints.MapGet(OptionsRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => GetOptionsAsync(principal, ct))
            .RequireCovePermission(Permissions.ExtensionsConfigure)
            .WithName("RenamerGetOptions")
            .WithSummary("Read the saved rename settings")
            .WithDescription(
                "Returns every member, filling any the stored document omits with its default. An unreadable stored document answers with the defaults and says so rather than failing.");

        // Binds the raw HttpContext so the pending-conversion refusal comes before the body is read and
        // a malformed body is this route's own 400. .Accepts<> is what puts the request schema in the
        // document.
        endpoints.MapPut(OptionsRoute,
            (HttpContext http, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => SaveOptionsAsync(http.Request, principal, ct))
            .Accepts<RenamerOptions>("application/json")
            .RequireCovePermission(Permissions.ExtensionsConfigure)
            .WithName("RenamerSaveOptions")
            .WithSummary("Replace the saved rename settings")
            .WithDescription(
                "The body replaces the whole document; a member it omits takes its default. Refused with 409 while a one-time conversion of the stored document is still pending.");
    }

    internal async Task<Results<Ok<OptionsView>, ForbiddenProblem>> GetOptionsAsync(
        ICurrentPrincipalAccessor principal, CancellationToken ct = default)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var stored = await Store.GetAsync(OptionsStore.Key, ct);
        var options = await StoredOptions.LoadAsync(ct);

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
    // defers until Cove has supplied the entity rows or library paths it needs, so this state outlives
    // a restart and is not a race.
    internal async Task<Results<NoContent, BadRequestProblem, ConflictProblem, ForbiddenProblem>> SaveOptionsAsync(
        HttpRequest request, ICurrentPrincipalAccessor principal, CancellationToken ct = default)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var stored = await Store.GetAsync(OptionsStore.Key, ct);
        if (OptionsMigration.Scan(stored).Any || OptionsMigration.HasLegacyDestinations(stored))
        {
            return new ConflictProblem("MIGRATION_PENDING", "A one-time conversion of the stored settings has not run yet.");
        }

        RenamerOptions? options;
        try
        {
            options = await JsonSerializer.DeserializeAsync<RenamerOptions>(
                request.Body, RenamerOptions.JsonOptions, ct);
        }
        catch (JsonException)
        {
            return new BadRequestProblem("INVALID_OPTIONS", "The request body is not a settings document.");
        }

        if (options is null)
        {
            return new BadRequestProblem("INVALID_OPTIONS", "The request body is not a settings document.");
        }

        // Repaired before the write, so the stored blob holds what every later load would answer and a
        // null list or a zero length cap never reaches the store.
        var optionsStore = StoredOptions;
        await optionsStore.SaveAsync(optionsStore.Repair(options), ct);
        return TypedResults.NoContent();
    }

    // Cove's configured library paths: the list every destination root is chosen from. Reads
    // in-memory host settings, so it opens no scope and touches no database. Gated on any
    // renamer-read permission: the list says where a rename may write, not what the library holds.
    internal Results<Ok<LibraryPathsView>, ForbiddenProblem> LibraryPaths(
        ICurrentPrincipalAccessor principal)
        => HasAnyReadPermission(principal)
            ? TypedResults.Ok(new LibraryPathsView(LibraryRoots))
            : new ForbiddenProblem();

    private string OwnJobTypePrefix => "ext:" + Id + ":";

    private string OwnJobType(string name) => OwnJobTypePrefix + name;

    // Where one of this extension's own runs has got to. Cove gates its job route on unrestricted
    // read, so a scoped account is refused there even for a run it started, and this serves the same
    // few fields under the extension's own check.
    //
    // A job whose type does not carry OwnJobTypePrefix is reported as not found. Answering forbidden
    // would confirm that id names a real job, which is the fact the host's gate withholds.
    internal Results<Ok<RenamerJobStatus>, NotFound, ForbiddenProblem> JobStatus(
        string jobId, ICurrentPrincipalAccessor principal, IJobService jobs)
    {
        if (!HasAnyReadPermission(principal))
        {
            return new ForbiddenProblem();
        }

        // Asked for by owner where the host gives this caller one. A job's progress, sub-task text and
        // error name what someone is doing with their own library, and the host records the enqueueing
        // principal on every job started from a request, so a run is readable by the caller who started
        // it and reads as 404 to anyone else.
        //
        // With authentication disabled the host hands every request one bypass principal of kind
        // System, which JobOwner declines to name an owner for, so neither the job nor the reader has
        // one and asking by owner would refuse the only user there is. The unscoped read then
        // withholds nothing from anybody, because there is nobody else.
        var owner = JobOwner.FromPrincipal(principal.Current);
        var job = owner is null ? jobs.GetJob(jobId) : jobs.GetJobFor(owner, jobId, includeAll: false);
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
    internal async Task<Results<Ok<OrphanedRulesView>, ForbiddenProblem>> OrphanedRulesAsync(
        ICurrentPrincipalAccessor principal, CancellationToken ct = default)
    {
        if (!HasAnyReadPermission(principal))
        {
            return new ForbiddenProblem();
        }

        var options = await StoredOptions.LoadAsync(ct);
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

    // The synchronous dry run: plans each requested id and mutates nothing.
    internal async Task<Results<Ok<PreviewResponse>, BadRequestProblem, ForbiddenProblem>> PreviewAsync(
        RenamerRequest req, DbContext db, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        // The kind is resolved first so the check gates on that kind's read permission. An unparseable
        // kind is a 400 before the check; it carries no ids and reads no data.
        if (!TryParseKind(req.EntityType, out var kind))
        {
            return new BadRequestProblem("UNSUPPORTED_ENTITY_TYPE", "This extension does not rename that kind of entity.");
        }

        var (readPermission, _) = PermissionsFor(kind);
        if (Forbidden(principal, readPermission) is { } denied)
        {
            return denied;
        }

        if (req.EntityIds is null)
        {
            return new BadRequestProblem("MISSING_ENTITY_IDS", "The request named no entities.");
        }

        if (req.EntityIds.Length > MaxEntityIdsPerRequest)
        {
            return new BadRequestProblem(
                "TOO_MANY_IDS",
                "The request named more entities than one call may carry.",
                MaxEntityIdsPerRequest);
        }

        var options = await StoredOptions.LoadAsync(ct);
        var port = new CoveRenamerDataPort(db, _coveConfig);
        var planner = new RenamerPlanner(port);

        // The batch's own lookups and loader, so the preview plans exactly what a run would. The walk
        // follows the caller's id order; an id the load did not return contributes nothing.
        var lookups = RouteLookups.From(options, LogInvalidRouteRegex);
        var loaded = await port.LoadEntitiesAsync(kind, req.EntityIds, ct);
        var byId = loaded.ToDictionary(e => e.EntityId);

        var items = new List<RenamerPlanItem>();
        var sizeByFileId = new Dictionary<int, long>();
        foreach (var id in req.EntityIds)
        {
            ct.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(id, out var entity))
            {
                continue;
            }

            var plan = await planner.PlanLoadedEntity(entity, options, lookups, ct);
            items.AddRange(plan.Items);
            foreach (var file in entity.Files)
            {
                sizeByFileId[file.FileId] = file.SizeBytes;
            }
        }

        // The path budget is read from the loaded options once and handed to both halves of the
        // response below, so the aggregate's in-flight overflow count and the per-item flags cannot be
        // measured against different limits and disagree.
        var summary = BatchPreview.Summarize(items, sizeByFileId, options.FullPathMax);

        return TypedResults.Ok(
            new PreviewResponse(
                [.. items.Select(i => PreviewItemView.From(
                    i, BatchPreview.InFlightPathOverflows(i, options.FullPathMax)))],
                summary));
    }

    // Returns 403 before any enqueue.
    //
    // One id the caller cannot write refuses the whole request, and the 403 carries no body, so the
    // response names none of the ids that were denied. The per-entity decision runs in the request
    // scope, where the caller's principal is live, so it needs no snapshot.
    internal async Task<Results<Accepted<JobEnqueued>, BadRequestProblem, ForbiddenProblem>> RenamerEnqueue(
        RenamerRequest req, ICurrentPrincipalAccessor principal, IJobService jobs,
        IAuthorizationService authz, CancellationToken ct)
    {
        // The kind is resolved first so the check gates on that kind's write permission.
        if (!TryParseKind(req.EntityType, out var kind))
        {
            return new BadRequestProblem("UNSUPPORTED_ENTITY_TYPE", "This extension does not rename that kind of entity.");
        }

        var (_, writePermission) = PermissionsFor(kind);
        if (Forbidden(principal, writePermission) is { } denied)
        {
            return denied;
        }

        if (req.EntityIds is null)
        {
            return new BadRequestProblem("MISSING_ENTITY_IDS", "The request named no entities.");
        }

        if (req.EntityIds.Length > MaxEntityIdsPerRequest)
        {
            return new BadRequestProblem(
                "TOO_MANY_IDS",
                "The request named more entities than one call may carry.",
                MaxEntityIdsPerRequest);
        }

        var allowed = await EntityAccessGuard.AllowedOnlyAsync(
            authz, principal.Current, kind, writePermission, req.EntityIds, ct);
        if (allowed.Count != req.EntityIds.Length)
        {
            return new ForbiddenProblem();
        }

        // Exclusive: a rename batch mutates disk and database, so two batches running at once could
        // plan against each other's stale snapshots or target the same paths.
        var jobId = jobs.Enqueue(
            OwnJobType("renamer-batch"),
            $"[{Name}] Rename selected",
            (coreProgress, ct) => RunRenamerBatchAsync(kind, req.EntityIds, new HostProgress(coreProgress), ct),
            exclusive: true);

        return TypedResults.Accepted((string?)null, new JobEnqueued(jobId));
    }

    // The summary of the rename an undo would act on, totalled over every batch that rename opened,
    // so a whole-library run reports what one press puts back.
    //
    // Spent is derived from the aggregate, not stored: a row exists exactly while its file still
    // needs restoring, so "nothing remains" and "already undone" are one fact and cannot disagree.
    // Reads the batch rows only and never pages the row table, so the response size is fixed
    // whatever the rename's size.
    internal async Task<Results<Ok<LastBatchSummary>, ForbiddenProblem>> LastBatchAsync(
        ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        // The summary carries no paths and no kind, so any kind's read permission admits it.
        if (!HasAnyReadPermission(principal))
        {
            return new ForbiddenProblem();
        }

        await using var scope = ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();

        await using var journal = new CoveRevertJournal(db);

        // The same read /undo names its target with, so the line this feeds describes the work the
        // button will do.
        var summary = await journal.ReadUndoTargetAsync(ct);
        return TypedResults.Ok(new LastBatchSummary(
            HasBatch: summary is not null,
            Count: summary?.OriginalCount ?? 0,
            RemainingCount: summary?.Remaining ?? 0,
            UnrestorableCount: summary?.UnrestorableCount ?? 0,
            WrittenAtUtcTicks: summary?.OpenedAtUtcTicks ?? 0,
            // Read from the constant the purge measures against, so the moment the panel prints and the
            // moment the rows go are the same rule rather than two copies of it.
            ExpiresAtUtcTicks: summary is null
                ? 0
                : summary.Value.OpenedAtUtcTicks + CoveRevertJournal.RetentionWindow.Ticks,
            Consumed: summary is not null && summary.Value.Remaining == 0));
    }

    // One array, read by the route declaration and by the handler. An endpoint advertising one gate
    // to the host while enforcing another still passes every test that drives the handler directly.
    private static readonly string[] AnyReadPermissions =
        [.. RenamableKinds.All.Select(k => PermissionsFor(k).Read)];

    // The write gate, on the same terms.
    private static readonly string[] AnyWritePermissions =
        [.. RenamableKinds.All.Select(k => PermissionsFor(k).Write)];

    private static RenamerFileKind[] HeldKinds(ICurrentPrincipalAccessor principal, bool write) =>
        [.. RenamableKinds.All.Where(k => principal.Current is { } current
            && current.Has(write ? PermissionsFor(k).Write : PermissionsFor(k).Read))];

    private static bool HasAnyReadPermission(ICurrentPrincipalAccessor principal)
        => principal.Current is { } current && Array.Exists(AnyReadPermissions, current.Has);

    private static bool HasAnyWritePermission(ICurrentPrincipalAccessor principal)
        => principal.Current is { } current && Array.Exists(AnyWritePermissions, current.Has);

    // Who a stored whole-library result belongs to, in the host's own spelling: "user:{id}" or
    // "token:{id}". The host answers null for an anonymous or System principal, and for a user it can
    // name neither way. Null is one identity rather than a wildcard: on an instance with
    // authentication off every caller is that identity, and there is only one user to confuse.
    private static string? OwnerKeyOf(ICurrentPrincipalAccessor principal)
        => JobOwner.FromPrincipal(principal.Current)?.Key;

    private static bool IsOwnedBy(string? ownerKey, ICurrentPrincipalAccessor principal)
        => string.Equals(ownerKey, OwnerKeyOf(principal), StringComparison.Ordinal);

    // Enqueues the whole-library scan. The optional body carries the caller's current options for a
    // dry run on unsaved edits; with no body it scans the saved options. The candidate ids are
    // server-derived per kind inside the job, so MaxEntityIdsPerRequest has nothing to bound here.
    //
    // Returns 403 before any enqueue, then captures the principal's held read kinds into the job
    // closure: the detached job cannot re-resolve the principal, and this is how it applies the same
    // per-kind skip a partial-permission caller sees from a preview. A copy of the principal itself
    // is captured beside them, because holding a kind's read permission does not grant read access to
    // every entity of that kind and the job authorizes each candidate it derives.
    //
    // The run id is minted here rather than taken from Enqueue, whose id is not available to the job
    // body before it returns, and it is what the caller reads the figures back by. The owner key is
    // captured with it: the aggregate covers only the entities this caller could read, so it is this
    // caller's to read back and nobody else's.
    internal Results<Accepted<ScanEnqueued>, ForbiddenProblem> ScanLibraryEnqueue(
        ScanLibraryRequest? body, ICurrentPrincipalAccessor principal, IJobService jobs)
    {
        if (!HasAnyReadPermission(principal))
        {
            return new ForbiddenProblem();
        }

        // Parsed here, because the detached job cannot read the request.
        var overrideOptions = TryParseOptionsOverride(body?.Options);

        var readableKinds = HeldKinds(principal, write: false);
        var caller = EntityAccessGuard.Snapshot(principal.Current);
        var run = new ScanRun(Guid.NewGuid().ToString("N"), OwnerKeyOf(principal));

        var jobId = jobs.Enqueue(
            OwnJobType("scan-library"),
            $"[{Name}] Scan library",
            (coreProgress, ct) => RunScanLibraryJobAsync(
                caller, readableKinds, overrideOptions, run, new HostProgress(coreProgress), ct),
            exclusive: true);

        return TypedResults.Accepted((string?)null, new ScanEnqueued(jobId, run.RunId));
    }

    // Returns null when the blob is absent, blank or unparseable, so a corrupt override falls back
    // to the saved options and does not fail the scan. A blob that binds gets the repair a saved load
    // gets.
    private RenamerOptions? TryParseOptionsOverride(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
        {
            return null;
        }

        try
        {
            var bound = JsonSerializer.Deserialize<RenamerOptions>(optionsJson, RenamerOptions.JsonOptions);
            return bound is null ? null : StoredOptions.Repair(bound);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Reads back one whole-library scan's aggregate, or 404 when the stored scan is not runId or was
    // not run by this caller. Only the latest scan is kept, so a scan that completed after the
    // caller's reads as 404 rather than as the caller's own figures.
    //
    // The owner match is what keeps the figures honest. The scan authorizes every candidate against
    // the principal that started it, so the aggregate describes that principal's reach: two callers
    // holding videos.read can still be granted different videos, and a kind filter alone cannot tell
    // them apart. Summing per kind over the caller's readable kinds stays, because a run of the
    // caller's own may still span kinds they have since lost.
    //
    // A blob that will not parse, or one stamped with an unrecognised schema version, reads as "no
    // scan yet", so a future shape change costs the user one dry run and not a 500.
    internal async Task<Results<Ok<ScanSummaryView>, NotFound, ForbiddenProblem>> ScanLibraryResultAsync(
        string runId, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (!HasAnyReadPermission(principal))
        {
            return new ForbiddenProblem();
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

        if (summary is null
            || summary.SchemaVersion != ScanSummary.CurrentSchemaVersion
            || !string.Equals(summary.RunId, runId, StringComparison.Ordinal)
            || !IsOwnedBy(summary.OwnerKey, principal))
        {
            return TypedResults.NotFound();
        }

        var readableKinds = HeldKinds(principal, write: false);
        return TypedResults.Ok(ScanSummaryView.From(summary, readableKinds));
    }

    // Reads back one whole-library rename's counts, on the same terms as /last-scan: 404 when the
    // stored run is not runId, was not run by this caller, will not parse, or carries an unknown
    // schema version. The counts are stored per kind and summed over the kinds the caller may read.
    internal async Task<Results<Ok<LibraryRenameSummaryView>, NotFound, ForbiddenProblem>> LibraryRenameResultAsync(
        string runId, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (!HasAnyReadPermission(principal))
        {
            return new ForbiddenProblem();
        }

        var json = await Store.GetAsync(LastLibraryRenameSummaryKey, ct);
        if (string.IsNullOrEmpty(json))
        {
            return TypedResults.NotFound();
        }

        LibraryRenameSummary? summary;
        try
        {
            summary = JsonSerializer.Deserialize<LibraryRenameSummary>(json, PreviewResponseJsonOptions);
        }
        catch (JsonException)
        {
            return TypedResults.NotFound();
        }

        if (summary is null
            || summary.SchemaVersion != LibraryRenameSummary.CurrentSchemaVersion
            || !string.Equals(summary.RunId, runId, StringComparison.Ordinal)
            || !IsOwnedBy(summary.OwnerKey, principal))
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(LibraryRenameSummaryView.From(summary, HeldKinds(principal, write: false)));
    }

    // One page of the whole-library dry run's rows, planned on demand over only the kinds the caller
    // may read. A null body means the first page, unfiltered.
    //
    // A live preview, not a snapshot read: the page is planned with the options in the request,
    // falling back to the saved ones, so a caller sending the blob it sent to /scan-library sees rows
    // consistent with that scan without the scan storing them.
    internal async Task<Results<Ok<ScanRowsPage>, BadRequestProblem, ForbiddenProblem>> ScanRowsAsync(
        ScanRowsRequest? body, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (!HasAnyReadPermission(principal))
        {
            return new ForbiddenProblem();
        }

        if (!ScanBucket.TryParse(body?.Bucket, out var bucket))
        {
            return new BadRequestProblem("UNSUPPORTED_BUCKET", "That is not one of the result buckets.");
        }

        ScanCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(body?.Kind))
        {
            if (!TryParseKind(body.Kind, out var cursorKind))
            {
                return new BadRequestProblem("UNSUPPORTED_ENTITY_TYPE", "This extension does not rename that kind of entity.");
            }

            cursor = new ScanCursor(cursorKind, Math.Max(body.AfterEntityId ?? 0, 0));
        }

        var options = TryParseOptionsOverride(body?.Options) ?? await StoredOptions.LoadAsync(ct);
        var lookups = RouteLookups.From(options, LogInvalidRouteRegex);
        // A kind turned off is dropped before the walk, exactly as RunScanCoreAsync drops it. Left in,
        // it would fill the table with library-many rows saying the kind is off while the counts beside
        // that table exclude it. A cursor minted while the kind was on resumes at the next kind.
        var kinds = RenamableKinds.All
            .Where(k => principal.Current!.Has(PermissionsFor(k).Read) && options.IsKindEnabled(k))
            .ToArray();

        await using var scope = ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        var port = new CoveRenamerDataPort(db, _coveConfig);
        var pager = new ScanRowPager(new RenamerPlanner(port), port);

        var page = await pager.PageAsync(
            kinds, cursor, body?.Take ?? 0, new ScanRowFilter(body?.Query, bucket), options, lookups, ct);

        return TypedResults.Ok(page);
    }

    // Enqueues the whole-library rename. No body and no caller-supplied id array, on the same terms
    // as the scan. Returns 403 before any enqueue, then captures the principal's held write kinds
    // into the job closure, because the detached job cannot re-resolve the principal. A copy of the
    // principal is captured beside them, because holding a kind's write permission does not grant
    // write access to every entity of that kind and the job authorizes each candidate it derives.
    internal Results<Accepted<LibraryRenameEnqueued>, ForbiddenProblem> RenamerLibraryEnqueue(
        ICurrentPrincipalAccessor principal, IJobService jobs)
    {
        if (!HasAnyWritePermission(principal))
        {
            return new ForbiddenProblem();
        }

        var writableKinds = HeldKinds(principal, write: true);
        var caller = EntityAccessGuard.Snapshot(principal.Current);
        var runId = Guid.NewGuid().ToString("N");
        var ownerKey = OwnerKeyOf(principal);

        var jobId = jobs.Enqueue(
            OwnJobType("renamer-library"),
            $"[{Name}] Rename library",
            (coreProgress, ct) => RunRenamerLibraryJobAsync(
                caller, writableKinds, new HostProgress(coreProgress), ct, runId: runId, ownerKey: ownerKey),
            exclusive: true);

        return TypedResults.Accepted((string?)null, new LibraryRenameEnqueued(jobId, runId));
    }

    // Runs the template engine over fixed samples with the in-flight options from the request body.
    // Selection-less and pure: no planner, no database, no disk, so a hostile template cannot escape
    // or amplify. The permission is enforced before any body read or engine work. An empty or
    // null-options body takes the defaults; malformed JSON is a 400.
    internal async Task<Results<Ok<IReadOnlyList<PreviewSampleResult>>, BadRequestProblem, ForbiddenProblem>> PreviewSampleAsync(
        HttpRequest httpReq, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        // The samples are fixed and touch no library data, so any kind's read permission admits them.
        if (!HasAnyReadPermission(principal))
        {
            return new ForbiddenProblem();
        }

        // Read to a string first to tell "no content", which takes the defaults, from "content
        // present but malformed", which is a 400. System.Text.Json throws on a zero-length stream.
        string body;
        using (var reader = new StreamReader(httpReq.Body, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync(ct);
        }

        RenamerOptions? options;
        if (string.IsNullOrWhiteSpace(body))
        {
            options = null;
        }
        else
        {
            PreviewSampleRequest? req;
            try
            {
                req = JsonSerializer.Deserialize<PreviewSampleRequest>(body, RenamerOptions.JsonOptions);
            }
            catch (JsonException)
            {
                return new BadRequestProblem("INVALID_BODY", "The request body could not be read.");
            }

            // A body carrying no Options takes the defaults too.
            options = req?.Options;
        }

        options = options is null ? new RenamerOptions() : StoredOptions.Repair(options);

        var results = SampleTokenSets.All
            .Select(sample => RenderSample(sample, options))
            .ToList();

        return TypedResults.Ok<IReadOnlyList<PreviewSampleResult>>(results);
    }

    // The dropped field names come from the engine, never a string diff.
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

    // Adapts the host's core IJobProgress, handed to the IJobService.Enqueue delegate, to the
    // extension IJobProgress the batch methods consume.
    private sealed class HostProgress(Cove.Core.Interfaces.IJobProgress core) : Cove.Plugins.IJobProgress
    {
        public void Report(double percent, string? message = null) => core.Report(percent, message);
    }
}
