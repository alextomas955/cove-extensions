using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    // The endpoint reference and the mapped route MUST be the same literal, so derive both from one
    // base. Instance members because Id comes from extension.json: reading a route before the host has
    // applied the manifest throws instead of mounting the endpoints under the wrong id.
    private string RouteBase => "/api/extensions/" + Id;
    private string HostConfigurationRoute => RouteBase + "/host-configuration";
    private string ConnectionTestRoute => RouteBase + "/connection/test";

    /// <summary>
    /// Registers every endpoint, each DECLARING the gate its own handler re-checks.
    /// </summary>
    /// <remarks>
    /// The declaration is what the host reads and audits; the in-handler check stays because the
    /// host's <c>[RequiresPermission]</c> filter is MVC-only and inert on a minimal-API endpoint, so
    /// the declaration alone enforces nothing on a host predating policy enforcement.
    /// </remarks>
    public override void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(HostConfigurationRoute,
            (ICurrentPrincipalAccessor principal) => HostConfiguration(principal))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);

        endpoints.MapPost(ConnectionTestRoute,
            (ConnectionTestRequest request, ICurrentPrincipalAccessor principal,
             IWhisparrConnectionTester tester, CancellationToken ct)
                => ConnectionTestAsync(request, principal, tester, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

    /// <summary>The tag every route of this extension carries in the emitted wire document.</summary>
    /// <remarks>
    /// Stated rather than inferred. The inferred tag comes from the handler's declaring type, and
    /// falls back to the ENTRY assembly for a handler that captures nothing — which is whichever
    /// process emitted the document, so an inferred tag moves the committed document the day the test
    /// runner changes.
    /// </remarks>
    private const string WireTag = "WhisparrSync";

    /// <summary>The settings tab this extension mounts, and the tab its one section targets.</summary>
    private const string SettingsTabKey = "whisparr-sync";

    /// <summary>
    /// The settings surface the host mounts: one dedicated tab under the Extensions settings group.
    /// </summary>
    /// <remarks>
    /// Page layout, so the host renders the panel full-width with no card chrome and this extension
    /// draws its own. <c>componentName</c> must be byte-identical to the key in the bundle's
    /// <c>defineExtension</c> component map: the host resolves one to the other by exact string and
    /// renders nothing, with no error, when they differ.
    /// </remarks>
    public override UIManifest GetUIManifest()
        => ManifestBuilder()
            .AddSettingsTab(
                key: SettingsTabKey,
                label: "Whisparr Sync",
                description: "Keep Cove in step with the Whisparr instance you configure.",
                order: 100,
                layout: SettingsTabLayout.Page)
            .AddSettingsSection(
                targetTab: SettingsTabKey,
                label: "Whisparr Sync",
                componentName: "WhisparrSyncPage")
            .WithJsBundle("index.mjs")
            .Build();

    /// <summary>
    /// What this extension can see of the host's own configuration from inside its container.
    /// </summary>
    /// <remarks>
    /// Opens no scope and touches no database: the answer is in-memory host state rather than library
    /// data.
    /// </remarks>
    internal Results<Ok<HostConfigurationView>, ForbiddenCode> HostConfiguration(
        ICurrentPrincipalAccessor principal)
        => HasReadPermission(principal)
            ? TypedResults.Ok(new HostConfigurationView(ConfigurationResolved, LibraryRootCount))
            : new ForbiddenCode();

    /// <summary>
    /// Tests one Whisparr address and key, and reports which of the six outcomes it produced.
    /// </summary>
    /// <remarks>
    /// The gate is checked BEFORE the body is read, so a principal without it causes no outbound
    /// request. Without that ordering the route would forward a request on behalf of a caller who is
    /// not allowed to configure this extension, and the classified answer would tell them what sits
    /// at an address they chose.
    /// </remarks>
    internal static async Task<Results<Ok<ConnectionTestView>, ForbiddenCode>> ConnectionTestAsync(
        ConnectionTestRequest request,
        ICurrentPrincipalAccessor principal,
        IWhisparrConnectionTester tester,
        CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tester);
        return TypedResults.Ok(await tester.TestAsync(request.Address, request.ApiKey, ct).ConfigureAwait(false));
    }

    /// <summary>The gates this extension's routes declare, and the ones their handlers re-check.</summary>
    /// <remarks>
    /// ONE array per tier, read by both, because the divergence is what would go unnoticed: an
    /// endpoint advertising one gate to the host while enforcing another still passes every test that
    /// drives the handler directly.
    /// </remarks>
    private static readonly string[] ReadPermissions = [Permissions.VideosRead];

    /// <inheritdoc cref="ReadPermissions"/>
    /// <remarks>
    /// The configure tier. No default Viewer or Member role holds it, which is what keeps the
    /// connection test out of reach of a caller who could otherwise aim it at an internal address.
    /// </remarks>
    private static readonly string[] ConfigurePermissions = [Permissions.ExtensionsConfigure];

    private static bool HasReadPermission(ICurrentPrincipalAccessor principal)
        => principal.Current is { } current && Array.Exists(ReadPermissions, current.Has);

    private static bool HasConfigurePermission(ICurrentPrincipalAccessor principal)
        => principal.Current is { } current && Array.Exists(ConfigurePermissions, current.Has);
}
