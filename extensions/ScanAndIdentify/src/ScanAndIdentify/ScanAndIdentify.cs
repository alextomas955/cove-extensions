using Cove.Core.Interfaces;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace ScanAndIdentify;

public sealed partial class ScanAndIdentify : FullExtensionBase
{
    // Identity and metadata come from extension.json, which the host applies through
    // IManifestAware.ApplyManifest before reading any of them. A property declared here overrides the
    // manifest silently.

    // Assigned by the host lifecycle hooks below, not the constructor, hence nullable.
    private string? _coveVersion;
    private bool _jobServiceAvailable;
    private bool _scanServiceAvailable;

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _coveVersion = context.CoveVersion;
    }

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        // GetService, not GetRequiredService: the capabilities route exists to report what resolved, so
        // a missing registration has to be reportable rather than fatal at load.
        _jobServiceAvailable = services.GetService<IJobService>() is not null;
        _scanServiceAvailable = services.GetService<IScanService>() is not null;
        return Task.CompletedTask;
    }
}
