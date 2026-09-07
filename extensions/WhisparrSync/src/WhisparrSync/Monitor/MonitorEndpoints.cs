using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Monitor;
using WhisparrSync.Options;
using static WhisparrSync.Contracts.WireSerializers;

namespace WhisparrSync;

/// <summary>
/// The studio/performer monitor slice's minimal-API surface: the monitor toggle + quiet-status projection,
/// and the studios/performers card-badge + toolbar-count reads. Gating here is capability-by-presence
/// (<see cref="IWhisparrPerformerMonitor"/>), never a raw version branch — the residual v2/v3 fetch+classify
/// split lives in the sibling <c>EntityStatusProjection.cs</c>, not in this endpoint file. Also hosts the
/// entity-vocabulary parse + identity-resolution helpers the monitor + push slices share.
/// </summary>
public sealed partial class WhisparrSync
{
    // The studio/performer monitor surface. /monitor toggles the monitored state
    // (add-then-flip via EntityMonitor); /monitor-status projects the quiet-status counts. Both are
    // mutating-posture (they reach the stored creds to call Whisparr), so both are configure-gated + stored-
    // creds-only: the request body carries the entity's OWN Cove remoteIds, never a url/key.
    private const string MonitorRoute = RouteBase + "/monitor";
    private const string MonitorStatusRoute = RouteBase + "/monitor-status";

    // POST {Kind, CoveEntityIds:[...]} → { states } for a studios/performers page — two Whisparr fetches, not per card.
    private const string EntityStatusBatchRoute = RouteBase + "/entity-status-batch";
    private const string EntityLibrarySummaryRoute = RouteBase + "/entity-library-summary";

    /// <summary>
    /// Registers the monitor slice's routes. All declare the configure tier and are stored-creds-only; the body carries the entity's own Cove remoteIds or ids, never a url/key.
    /// </summary>
    private void MapMonitorEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Studio/performer monitor toggle + status. Both POST (they carry the entity's
        // Cove remoteIds in the body) and delegate to an extracted instance handler so they are unit-testable
        // host-free. Stored creds only: the body carries NO url/key.
        endpoints.MapPost(MonitorRoute,
            (MonitorRequest req, WhisparrClient client, CancellationToken ct)
                => MonitorAsync(req, client, ct)).ConfigureGated();

        endpoints.MapPost(MonitorStatusRoute,
            (MonitorStatusRequest req, WhisparrClient client, CancellationToken ct)
                => MonitorStatusAsync(req, client, ct)).ConfigureGated();

        endpoints.MapPost(EntityStatusBatchRoute,
            (EntityStatusBatchRequest req, WhisparrClient client, CancellationToken ct)
                => EntityStatusBatchAsync(req, client, ct)).ConfigureGated();

        endpoints.MapGet(EntityLibrarySummaryRoute,
            (string? kind, WhisparrClient client, CancellationToken ct)
                => EntityLibrarySummaryAsync(kind, client, ct)).ConfigureGated();
    }

    /// <summary>
    /// Toggles the monitor state of a studio or performer in Whisparr via the
    /// add-then-flip semantics (<see cref="EntityMonitor.SetMonitorAsync"/>). Configure-gated: this
    /// mutation reaches the stored credentials, so a read-only principal must not reach it. Stored creds ONLY:
    /// the request body carries NO url/key — <see cref="ResolveCredsAsync"/> with an empty
    /// request resolves the stored host+key, so the stored key is never paired with a caller-supplied host and
    /// is never echoed. The Whisparr lookup id is resolved server-side from the entity's forwarded Cove
    /// <c>remoteIds</c> by the CONNECTED version's endpoint (StashDB on v3, ThePornDB on v2 —
    /// <see cref="WhisparrOptions.IdentityEndpoint"/>), so a caller cannot point the toggle at an arbitrary id.
    /// A v2 studio routes to the real SITE add-then-flip; a v2 performer defers (VersionMismatch → a clear 400,
    /// never a 500). Never triggers a Whisparr search (loop-safety is enforced in the adapter).
    /// </summary>
    internal async Task<IResult> MonitorAsync(
        MonitorRequest req, WhisparrClient client, CancellationToken ct)
    {
        if (!TryParseEntityKind(req.Kind, out var kind))
        {
            return Results.Json(new ErrorResponse("UNKNOWN_KIND"), statusCode: 400);
        }

        var (options, _, _) = await StoredCredsAsync(ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            return VersionUnsupported();
        }

        // Capability precedes identity (matches MonitorStatusAsync): a performer on v2 is unsupported id or not.
        if ((kind != EntityKind.Studio && adapter is not IWhisparrPerformerMonitor))
        {
            return Results.Json(new VersionUnsupportedResponse("VERSION_UNSUPPORTED", options.SelectedVersion), statusCode: 400);
        }

        if (ResolveRemoteId(req.RemoteIds, options) is not { } stashId)
        {
            // The entity carries no identity on the connected version's endpoint — it cannot be monitored, and
            // we must NOT call Whisparr with a wrong id. A clear handled outcome, not an error. A v2 studio that
            // DOES resolve routes to the real SITE add-then-flip (the adapter's VersionMismatch, if any, maps to
            // a clear 400 via ToMonitorResult — never a 500).
            return Results.Json(new NoIdentityResponse("NO_STASHDB_IDENTITY", ProviderNameFor(options)), EnumStringResponseJsonOptions);
        }

        // Refuse an incomplete stored configuration here, BEFORE the orchestration seam is constructed — that
        // seam's first acts are the fallback-root read and the origin-tag find-or-create, so a guard placed inside
        // it would already have issued two wire calls and could leave a stray cove-sync tag behind.
        if (RefuseIncompleteConfig(options) is { } refusal)
        {
            return refusal;
        }

        // Scope follows the request when supplied, else the stored default. Unparseable → the default (never a
        // throw). Ignored when turning monitor off (the adapter unmonitors the scenes regardless of scope).
        var scope = ParseMonitorScope(req.Scope, options.DefaultMonitorScope);
        var result = await new EntityMonitor(client, options).SetMonitorAsync(kind, stashId, req.Monitored, scope, ct);
        if (result.IsOk)
        {
            LogMonitorToggled(kind, req.Monitored, result.Value!.Added);
        }

        return ToMonitorResult(result);
    }

    /// <summary>
    /// Projects the quiet-status ("added / monitored / X of Y grabbed") for a studio/performer via
    /// <see cref="EntityMonitor.GetStatusAsync"/>. Same security posture as <see cref="MonitorAsync"/>:
    /// configure-gated, stored creds only (it reaches the stored credentials to read the Whisparr movie set),
    /// server-side stashId resolution, and a graceful v2 deferral. Reads only — no Cove or Whisparr mutation.
    /// </summary>
    internal async Task<IResult> MonitorStatusAsync(
        MonitorStatusRequest req, WhisparrClient client, CancellationToken ct)
    {
        if (!TryParseEntityKind(req.Kind, out var kind))
        {
            return Results.Json(new ErrorResponse("UNKNOWN_KIND"), statusCode: 400);
        }

        var (options, _, _) = await StoredCredsAsync(ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            return VersionUnsupported();
        }

        // Capability precedes identity. A performer on v2 has no entity in the version at all; a missing id is
        // beside the point. Without this guard an id-less performer returns NO_STASHDB_IDENTITY (a disabled
        // "add a metadata link" control) when it should return VERSION_UNSUPPORTED (the state the UI hides on).
        if ((kind != EntityKind.Studio && adapter is not IWhisparrPerformerMonitor))
        {
            return Results.Json(new VersionUnsupportedResponse("VERSION_UNSUPPORTED", options.SelectedVersion), statusCode: 400);
        }

        if (ResolveRemoteId(req.RemoteIds, options) is not { } stashId)
        {
            return Results.Json(new NoIdentityResponse("NO_STASHDB_IDENTITY", ProviderNameFor(options)), EnumStringResponseJsonOptions);
        }

        var result = await new EntityMonitor(client, options).GetStatusAsync(kind, stashId, ct);
        if (result.State is not WhisparrResultState.Ok)
        {
            return ToMonitorResult(result);
        }

        // The bulk menu gates on these: "Add all missing" needs IWhisparrScenePush (v3), "Reflect owned" needs
        // IWhisparrOwnedImport (both versions). Without them the menu would offer an action the connected version
        // answers with 400.
        var status = result.Value!;
        return Results.Json(
            new MonitorStatusResponse(
                status.Added,
                status.Monitored,
                status.ScenesPresent,
                status.ScenesTotal,
                status.HasCounts,
                AddSupported: adapter is IWhisparrScenePush,
                OwnedImportSupported: adapter is IWhisparrOwnedImport),
            EnumStringResponseJsonOptions);
    }

    /// <summary>
    /// Parses the request <c>Kind</c> (<c>"studio"</c> / <c>"performer"</c> / <c>"tag"</c>, case-insensitive)
    /// into the <see cref="EntityKind"/>. Returns <c>false</c> for anything else so the caller rejects an
    /// unknown kind with a 400 rather than guessing.
    /// </summary>
    private static bool TryParseEntityKind(string? kind, out EntityKind entityKind)
    {
        if (string.Equals(kind, "studio", StringComparison.OrdinalIgnoreCase))
        {
            entityKind = EntityKind.Studio;
            return true;
        }

        if (string.Equals(kind, "performer", StringComparison.OrdinalIgnoreCase))
        {
            entityKind = EntityKind.Performer;
            return true;
        }

        if (string.Equals(kind, "tag", StringComparison.OrdinalIgnoreCase))
        {
            entityKind = EntityKind.Tag;
            return true;
        }

        entityKind = default;
        return false;
    }

    // Parse the wire scope ("NewReleases"/"AllScenes", case-insensitive) to the enum; any absent or
    // unrecognized value falls back to the stored default so a malformed body never throws.
    private static MonitorScope ParseMonitorScope(string? scope, MonitorScope fallback)
        => Enum.TryParse<MonitorScope>(scope, ignoreCase: true, out var parsed) ? parsed : fallback;

    /// <summary>
    /// Resolves the entity's Whisparr lookup id from the forwarded Cove <paramref name="remoteIds"/>: the
    /// <c>RemoteId</c> whose <c>Endpoint</c> case-insensitively equals the stored
    /// <paramref name="stashDbEndpoint"/> — the SAME rule <see cref="Library.CoveLibraryPort"/> uses to resolve a
    /// video's StashDB id, keeping the endpoint match a single server-side source of truth. Returns
    /// <c>null</c> when the entity carries no StashDB identity (the caller reports it cannot be monitored).
    /// </summary>
    private static string? ResolveStashId(RemoteIdInput[]? remoteIds, string stashDbEndpoint)
        => remoteIds?
            .FirstOrDefault(r =>
                !string.IsNullOrWhiteSpace(r.RemoteId)
                && string.Equals(r.Endpoint, stashDbEndpoint, StringComparison.OrdinalIgnoreCase))
            ?.RemoteId;

    /// <summary>
    /// Resolves the entity's Whisparr lookup id by the CONNECTED version's endpoint
    /// (<see cref="WhisparrOptions.IdentityEndpoint"/>): StashDB on v3, ThePornDB on v2. The version selection is
    /// the layer over the <see cref="ResolveStashId"/> endpoint-match primitive; a v2 connection therefore
    /// resolves the TPDB site id, never a StashDB id the v2 instance cannot know. Returns <c>null</c> when the
    /// entity carries no id for the connected version (the caller reports the handled no-identity outcome).
    /// </summary>
    private static string? ResolveRemoteId(RemoteIdInput[]? remoteIds, WhisparrOptions options)
        => ResolveStashId(remoteIds, options.IdentityEndpoint);

    // The connected version's identity provider name — the name the missing-id guard + Lifecycle surface to
    // the user. Derived from SelectedVersion (v3 keys on StashDB, v2 on ThePornDB), never a hardcoded literal
    // at a call site, so a v2 connection is never mislabeled "StashDB".
    private static string ProviderNameFor(WhisparrOptions options)
        => string.Equals(options.SelectedVersion, "v2", StringComparison.OrdinalIgnoreCase) ? "ThePornDB" : "StashDB";
}
