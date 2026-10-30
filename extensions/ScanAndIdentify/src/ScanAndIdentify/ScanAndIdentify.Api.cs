using System.Text.Json;
using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using ScanAndIdentify.Contracts;
using ScanAndIdentify.Options;
using static Cove.Extensions.Shared.MinimalApiPermissions;

namespace ScanAndIdentify;

public sealed partial class ScanAndIdentify
{
    // The route prefix mirrors how the host mounts an extension's IApiExtension endpoints:
    // /api/extensions/{id}/…
    //
    // Instance members because Id comes from extension.json: reading a route before the host has
    // applied the manifest throws instead of mounting the endpoints under the wrong id.
    private string RouteBase => "/api/extensions/" + Id;
    private string CapabilitiesRoute => RouteBase + "/capabilities";
    private string OptionsRoute => RouteBase + "/options";

    private RunOptionsStore StoredOptions => new(Store, _log);

    // The componentName has to equal the key in the bundle's defineExtension components map, or the
    // host mounts nothing and reports nothing.
    public override UIManifest GetUIManifest()
        => ManifestBuilder()
            .AddSettingsTab(
                key: "scan-and-identify",
                label: "Scan and Identify",
                layout: SettingsTabLayout.Panels,
                order: 100)
            .AddSettingsSection(
                targetTab: "scan-and-identify",
                label: "Scan and Identify",
                componentName: "ScanAndIdentifyPage")
            .WithJsBundle("index.mjs")
            .Build();

    // The response carries the host version and two resolution flags, no library data and no
    // configuration, so it needs no permission. An endpoint declaring none of the SDK's authorization
    // conventions is treated as anonymous and only warned about at host boot.
    public override void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(CapabilitiesRoute, () => Capabilities())
            .AllowWithoutCovePermission();

        // Gated on extensions.configure, not on a media permission: this one document decides what a
        // later run does to the whole library, and it is the permission Cove's own extension-data
        // routes carry over the same bytes.
        endpoints.MapGet(OptionsRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => GetOptionsAsync(principal, ct))
            .RequireCovePermission(Permissions.ExtensionsConfigure);

        // Binds the raw HttpContext so a malformed body is this route's own 400 rather than a
        // framework 400 with no schema. .Accepts<> is what puts the request shape in the document.
        endpoints.MapPut(OptionsRoute,
            (HttpContext http, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => SaveOptionsAsync(http.Request, principal, ct))
            .Accepts<RunOptions>("application/json")
            .RequireCovePermission(Permissions.ExtensionsConfigure);
    }

    internal CapabilitiesView Capabilities()
        => new(_coveVersion, _jobServiceAvailable, _scanServiceAvailable);

    // The saved description of a run, read through the same store a run will read it through, so the
    // panel and a run can never disagree about what a stored blob means.
    internal async Task<Results<Ok<RunOptions>, ForbiddenCode>> GetOptionsAsync(
        ICurrentPrincipalAccessor principal, CancellationToken ct = default)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        return TypedResults.Ok(await StoredOptions.LoadAsync(ct));
    }

    // Persists the options in the spelling the store owns rather than the wire's.
    //
    // The stored document is replaced, not merged: what goes back is the members RunOptions declares.
    // A property only a newer version knows about is readable here, because a load ignores what it
    // does not recognize, but it does not survive this write.
    internal async Task<Results<NoContent, BadRequest<ErrorCode>, ForbiddenCode>> SaveOptionsAsync(
        HttpRequest request, ICurrentPrincipalAccessor principal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        RunOptions? options;
        try
        {
            options = await JsonSerializer.DeserializeAsync<RunOptions>(
                request.Body, RunOptions.JsonOptions, ct);
        }
        catch (JsonException)
        {
            return TypedResults.BadRequest(new ErrorCode("INVALID_OPTIONS"));
        }

        if (options is null)
        {
            return TypedResults.BadRequest(new ErrorCode("INVALID_OPTIONS"));
        }

        // Repaired before the write, so the stored blob holds what every later load would answer.
        var optionsStore = StoredOptions;
        await optionsStore.SaveAsync(optionsStore.Repair(options), ct);
        return TypedResults.NoContent();
    }
}
