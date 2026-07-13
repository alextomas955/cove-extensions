using System.Text.Json;
using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WhisparrSync.Client;

namespace WhisparrSync;

/// <summary>
/// The Cove-facing surface of the extension: the full-page settings tab (contributed through
/// <see cref="GetUIManifest"/>) and the <c>/test-connection</c> minimal-API endpoint. Stays disjoint
/// from <c>WhisparrSync.cs</c> (identity + DI) and <c>WhisparrSync.Logging.cs</c>.
/// </summary>
public sealed partial class WhisparrSync
{
    // The route prefix mirrors how the host mounts an extension's endpoints: /api/extensions/{id}/…
    private const string RouteBase = "/api/extensions/com.alextomas955.whisparrsync";
    private const string TestConnectionRoute = RouteBase + "/test-connection";

    /// <summary>
    /// Contributes the Whisparr Sync settings tab as a full-page layout (like Renamer): the host renders
    /// the tab's panel full-width with no card chrome and the extension owns the canvas. The section's
    /// <c>componentName</c> ("WhisparrSyncPage") MUST equal the key in the bundle's <c>defineExtension</c>
    /// components map (see the UI <c>index.ts</c>).
    /// </summary>
    public override UIManifest GetUIManifest()
        => ManifestBuilder()
            .AddSettingsTab(
                key: "whisparr-sync",
                label: "Whisparr Sync",
                description: "Connect Cove to your Whisparr instance.",
                order: 100,
                layout: SettingsTabLayout.Page)
            .AddSettingsSection(targetTab: "whisparr-sync", label: "Whisparr Sync", componentName: "WhisparrSyncPage")
            .WithJsBundle("index.mjs")
            .Build();

    /// <summary>
    /// Maps the settings endpoints. The lambda immediately delegates to an extracted instance method so
    /// the handler is unit-testable without an HTTP host. The host resolves the typed
    /// <see cref="WhisparrClient"/> and <see cref="ICurrentPrincipalAccessor"/> from the request scope.
    /// </summary>
    public override void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(TestConnectionRoute,
            (TestConnectionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => TestConnectionAsync(req, client, principal, ct));
    }

    /// <summary>
    /// Tests the connection to the supplied Whisparr URL + API key and returns the detected version +
    /// instance name on success. 403-first on <c>extensions.configure</c> (the host
    /// <c>[RequiresPermission]</c> filter is inert on minimal-API, so re-check here BEFORE any outbound
    /// call). The API key is used server-side only and is never included in the response (CONN-06).
    /// </summary>
    internal async Task<IResult> TestConnectionAsync(
        TestConnectionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (principal.Current is null || !principal.Current.Has(Permissions.ExtensionsConfigure))
        {
            return Results.Json(new { code = "FORBIDDEN" }, statusCode: 403);
        }

        var result = await client.GetStatusAsync(req.BaseUrl ?? string.Empty, req.ApiKey ?? string.Empty, ct);
        if (result.IsOk)
        {
            var status = result.Value!;
            LogConnectTested(status.Version ?? "unknown", status.InstanceName ?? "unknown");
            return Results.Json(
                new { status = "connected", version = status.Version, instanceName = status.InstanceName },
                TestConnectionResponseJsonOptions);
        }

        LogWhisparrUnreachable(result.Reason ?? result.State.ToString());
        return Results.Json(
            new { status = "error", error = result.State.ToString(), reason = result.Reason },
            TestConnectionResponseJsonOptions);
    }

    // Serialize responses with the extension's own web-convention options so the wire shape (camelCase)
    // matches what the UI reads; the host's default minimal-API serializer is not relied on here.
    private static readonly JsonSerializerOptions TestConnectionResponseJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The Test-connection request body: the URL + key the user typed on the settings page.</summary>
    internal sealed record TestConnectionRequest(string? BaseUrl, string? ApiKey);
}
