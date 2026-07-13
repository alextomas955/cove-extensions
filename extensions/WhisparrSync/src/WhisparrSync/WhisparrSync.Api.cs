using System.Text.Json;
using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WhisparrSync.Adapters;
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
    /// Runs the full connect flow against the supplied Whisparr URL + API key and returns a discriminated
    /// result the UI branches on (CONN-02): <c>ok</c> (with version + instance name), <c>badKey</c>,
    /// <c>unreachable</c> (with a short reason), <c>notWhisparr</c> (HTML/502), or <c>versionMismatch</c>
    /// (with the detected version — the fail-closed VER-04 refusal when the major version is not 3). The
    /// adapter is chosen from the parsed status via <see cref="AdapterSelector"/>, never from the status
    /// code (both v2 and v3 answer <c>/api/v3</c>). 403-first on <c>extensions.configure</c> (the host
    /// <c>[RequiresPermission]</c> filter is inert on minimal-API). The API key is used server-side only
    /// and is never included in the response (CONN-06).
    /// </summary>
    internal async Task<IResult> TestConnectionAsync(
        TestConnectionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (principal.Current is null || !principal.Current.Has(Permissions.ExtensionsConfigure))
        {
            return Results.Json(new { code = "FORBIDDEN" }, statusCode: 403);
        }

        var result = await client.GetStatusAsync(req.BaseUrl ?? string.Empty, req.ApiKey ?? string.Empty, ct);

        switch (result.State)
        {
            case WhisparrResultState.Ok:
                var status = result.Value!;
                // Branch on the parsed version, never the 200 status: a v2 instance also answers /api/v3.
                if (AdapterSelector.Select(status, client) is null)
                {
                    LogVersionRefused(AdapterSelector.ParseMajor(status.Version));
                    return Results.Json(
                        new { result = "versionMismatch", detected = status.Version },
                        TestConnectionResponseJsonOptions);
                }

                LogConnectTested(status.Version ?? "unknown", status.InstanceName ?? "unknown");
                return Results.Json(
                    new { result = "ok", version = status.Version, instanceName = status.InstanceName },
                    TestConnectionResponseJsonOptions);

            case WhisparrResultState.BadKey:
                return Results.Json(new { result = "badKey" }, TestConnectionResponseJsonOptions);

            case WhisparrResultState.NotWhisparr:
                return Results.Json(new { result = "notWhisparr" }, TestConnectionResponseJsonOptions);

            default:
                LogWhisparrUnreachable(result.Reason ?? result.State.ToString());
                return Results.Json(
                    new { result = "unreachable", reason = result.Reason },
                    TestConnectionResponseJsonOptions);
        }
    }

    // Serialize responses with the extension's own web-convention options so the wire shape (camelCase)
    // matches what the UI reads; the host's default minimal-API serializer is not relied on here.
    private static readonly JsonSerializerOptions TestConnectionResponseJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The Test-connection request body: the URL + key the user typed on the settings page.</summary>
    internal sealed record TestConnectionRequest(string? BaseUrl, string? ApiKey);
}
