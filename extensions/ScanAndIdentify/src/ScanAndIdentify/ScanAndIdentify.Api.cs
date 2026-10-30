using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using ScanAndIdentify.Contracts;

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
    }

    internal CapabilitiesView Capabilities()
        => new(_coveVersion, _jobServiceAvailable, _scanServiceAvailable);
}
