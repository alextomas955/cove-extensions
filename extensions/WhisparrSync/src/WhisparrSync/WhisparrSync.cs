using Cove.Plugins;
using Cove.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Connection;
using WhisparrSync.Whisparr;

using CoveConfiguration = Cove.Core.Interfaces.CoveConfiguration;

namespace WhisparrSync;

public sealed partial class WhisparrSync : FullExtensionBase
{
    // Identity and metadata come from extension.json, which the host applies to this instance
    // (IManifestAware.ApplyManifest) before it reads any of them. The host reads each value straight
    // off the property, so an override declared here overrides the manifest silently.

    private CoveConfiguration? _coveConfig;

    /// <summary>
    /// The host logger, writing to Cove's normal log. Non-null by construction: it defaults to a no-op
    /// logger and is replaced in <see cref="InitializeAsync"/> if the host supplies one, so the
    /// source-generated <c>[LoggerMessage]</c> methods never dereference null. (The generator binds to
    /// this field by its <see cref="ILogger"/> type.)
    /// </summary>
    private ILogger _log = NullLogger.Instance;

    /// <summary>
    /// Whether the host's own configuration object resolved out of this extension's service provider.
    /// </summary>
    private bool ConfigurationResolved => _coveConfig is not null;

    /// <summary>Cove's configured library paths, blank entries dropped.</summary>
    /// <remarks>
    /// A blank entry is not a root anything can be placed under, so counting one would report a
    /// library location that does not exist.
    /// </remarks>
    private int LibraryRootCount =>
        _coveConfig?.CovePaths.Count(path => !string.IsNullOrWhiteSpace(path.Path)) ?? 0;

    /// <summary>
    /// Registers this extension's own services into the container the host builds for it.
    /// </summary>
    /// <remarks>
    /// The outbound client is a TYPED client rather than a constructed <c>HttpClient</c>, so its
    /// handler is pooled and its lifetime is the factory's. The host stands the
    /// <c>AddHttpClient</c> stack up before calling this, which is what lets an extension register one
    /// at all.
    /// </remarks>
    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        base.ConfigureServices(services, context);

        services.AddHttpClient<IWhisparrClient, WhisparrClient>(WhisparrClient.Configure)
            .ConfigurePrimaryHttpMessageHandler(WhisparrClient.CreateHandler);

        services.AddScoped<IWhisparrConnectionTester, ConnectionTester>();
    }

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        // Logging first, so the configuration line below has somewhere to go. Optional (GetService,
        // not GetRequiredService): the host forwards ILogger into the extension scope, but its absence
        // must not stop the extension loading.
        _log = services.GetService<ILogger<WhisparrSync>>() ?? _log;

        // Optional for the same reason: a host that registers no configuration must still load the
        // extension. The cost is reported rather than silent.
        _coveConfig = services.GetService<CoveConfiguration>();
        if (_coveConfig is null)
        {
            LogNoCoveConfiguration();
        }

        return base.InitializeAsync(services, ct);
    }
}
