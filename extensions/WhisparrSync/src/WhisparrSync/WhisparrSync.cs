using Cove.Core.Interfaces;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Connection;
using WhisparrSync.Options;
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

    /// <summary>
    /// Whether the host's scan service could be obtained from this extension's container at load.
    /// </summary>
    private bool ScanServiceResolved { get; set; }

    /// <summary>
    /// Whether the host's metadata-server service could be obtained from this extension's container
    /// at load.
    /// </summary>
    private bool MetadataServerServiceResolved { get; set; }

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
    /// <para>
    /// The options store is registered as a factory over this instance rather than by its type: the
    /// host hands an extension its <c>IExtensionStore</c> through <c>IStatefulExtension.SetStore</c>
    /// and registers it in no container, so a type registration would resolve to nothing. The factory
    /// runs per scope, which is after the host has supplied one.
    /// </para>
    /// </remarks>
    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        base.ConfigureServices(services, context);

        services.AddHttpClient<IWhisparrClient, WhisparrClient>(WhisparrClient.Configure)
            .ConfigurePrimaryHttpMessageHandler(WhisparrClient.CreateHandler);

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IWhisparrConnectionTester, ConnectionTester>();
        services.AddScoped<IConnectionTestRunner, ConnectionTestRunner>();
        services.AddScoped<ICredentialPort, CredentialPort>();
        services.AddScoped<ICallbackSecretPort>(
            services => new CallbackSecretPort(services.GetRequiredService<DbContext>(), _log));
        services.AddScoped<IWhisparrNotificationPort>(
            services => new NotificationPort(services.GetRequiredService<IWhisparrClient>(), _log));
        services.AddScoped(_ => new OptionsStore(Store, _log));
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

        ScanServiceResolved = CanObtain<IScanService>(services);
        if (!ScanServiceResolved)
        {
            LogNoScanService();
        }

        MetadataServerServiceResolved = CanObtain<IMetadataServerService>(services);
        if (!MetadataServerServiceResolved)
        {
            LogNoMetadataServerService();
        }

        return base.InitializeAsync(services, ct);
    }

    /// <summary>
    /// Whether <typeparamref name="T"/> can be obtained from this extension's own container.
    /// </summary>
    /// <remarks>
    /// Resolved inside a scope rather than off <paramref name="services"/> directly. The host copies
    /// its own scoped registrations into the extension container and builds that container with scope
    /// validation on, so resolving one of them from the provider handed to
    /// <see cref="InitializeAsync"/> throws instead of answering, and a throw here disables the
    /// extension.
    /// <para>
    /// The instance is discarded rather than held: a scoped one kept in a field outlives the scope
    /// that created it. A registration that is present but cannot be constructed in this container is
    /// a service this extension cannot obtain, which is the reading the caller asked for, so the
    /// construction failure is an answer rather than a fault.
    /// </para>
    /// </remarks>
    private static bool CanObtain<T>(IServiceProvider services)
        where T : class
    {
        try
        {
            using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateScope();
            return scope.ServiceProvider.GetService<T>() is not null;
        }
#pragma warning disable CA1031 // Load-time probe: an unobtainable service is the answer, not a fault.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return false;
        }
    }
}
