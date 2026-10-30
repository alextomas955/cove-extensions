using Cove.Core.Interfaces;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    // The one place a user's whole stored configuration can be discarded in silence is the options
    // store's corrupt-blob arm, which says so through this logger and nowhere else.
    private ILogger _log = NullLogger.Instance;

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _coveVersion = context.CoveVersion;
    }

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Registration is asked about, never resolved. The host passes its root provider here, and
        // Cove registers IScanService per request, so resolving it throws "Cannot resolve scoped
        // service from root provider" and the host disables the extension. IsService answers whether
        // the host declared the service without constructing one. When the host supplies no probe at
        // all, nothing can be reported and the route says unavailable.
        // Safe from the root provider where IScanService is not: AddLogging registers the ILogger<>
        // open generic as a singleton, so there is no scope to resolve it out of.
        _log = services.GetService<ILogger<ScanAndIdentify>>() ?? _log;

        var registrations = services.GetService<IServiceProviderIsService>();
        _jobServiceAvailable = registrations?.IsService(typeof(IJobService)) ?? false;
        _scanServiceAvailable = registrations?.IsService(typeof(IScanService)) ?? false;
        return Task.CompletedTask;
    }
}
