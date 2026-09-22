using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Addressing;
using WhisparrSync.Connection;
using WhisparrSync.Import;
using WhisparrSync.Library;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Whisparr;
using CoveConfiguration = Cove.Core.Interfaces.CoveConfiguration;

namespace WhisparrSync;

public sealed partial class WhisparrSync : FullExtensionBase
{
    // Identity and metadata come from extension.json, which the host applies to this instance
    // (IManifestAware.ApplyManifest) before it reads any of them. The host reads each value straight
    // off the property, so an override declared here overrides the manifest silently.

    private CoveConfiguration? _coveConfig;

    // Defaults to a no-op logger so the generated [LoggerMessage] methods never dereference null.
    // The generator binds to this field by its ILogger type.
    private ILogger _log = NullLogger.Instance;

    // The stored generation's enum name, or null while none is established. Volatile because
    // GetUIManifest runs on host threads other than the one a settings save answers on, and a
    // string because volatile takes no nullable enum. Null registers every surface, so a load that
    // could not be made removes none.
    private volatile string? _selectedGeneration;

    private bool ConfigurationResolved => _coveConfig is not null;

    private bool ScanServiceResolved { get; set; }

    private bool MetadataServerServiceResolved { get; set; }

    // A blank path is not a root anything can be placed under, so counting one would report a
    // library location that does not exist.
    private int LibraryRootCount =>
        _coveConfig?.CovePaths.Count(path => !string.IsNullOrWhiteSpace(path.Path)) ?? 0;

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        base.ConfigureServices(services, context);

        // Singletons: a registration per request would be a handler pool per request.
        services.AddSingleton<Whisparr3Gateway>();
        services.AddSingleton<Whisparr2Gateway>();

        // Resolved off the instance's own lookup rather than the configured metadata source, so a
        // studio held under a provider's code costs no second request. Singleton like the gateway
        // it sends through: it holds nothing between calls and takes the instance per call.
        services.AddSingleton<ISiteNumberPort>(
            provider => new InstanceSiteNumberPort(
                provider.GetRequiredService<Whisparr2Gateway>()));

        // A typed client rather than a constructed HttpClient, so the handler is pooled and its
        // lifetime is the factory's. The host stands the AddHttpClient stack up before this call.
        services.AddHttpClient<IWhisparrClient, WhisparrClient>(WhisparrClient.Configure)
            .ConfigurePrimaryHttpMessageHandler(WhisparrClient.CreateHandler)

            // Built here rather than from the container, so the client writes to this extension's own
            // logger for the same reason every other service constructed above does.
            .AddTypedClient<IWhisparrClient>((client, services) => new WhisparrClient(
                client,
                services.GetRequiredService<Whisparr3Gateway>(),
                services.GetRequiredService<Whisparr2Gateway>(),
                services.GetRequiredService<ISiteNumberPort>(),
                _log));

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IWhisparrConnectionTester, ConnectionTester>();
        services.AddScoped<IConnectionTestRunner, ConnectionTestRunner>();
        services.AddScoped<ICredentialPort, CredentialPort>();
        services.AddScoped<IHostLockdownPort>(services => new HostLockdownPort(
            services.GetService<CoveConfiguration>(), services.GetService<IUserService>()));
        services.AddScoped<ICallbackSecretPort>(
            services => new CallbackSecretPort(services.GetRequiredService<DbContext>(), _log));
        services.AddScoped<IWhisparrNotificationPort>(
            services => new NotificationPort(services.GetRequiredService<IWhisparrClient>(), _log));
        services.AddScoped(_ => NewOptionsStore());
        services.AddScoped<IEntityIdentityPort>(services => new EntityIdentityPort(
            services.GetRequiredService<DbContext>(),
            services.GetRequiredService<OptionsStore>()));
        services.AddScoped<IEntityFolderPort>(
            services => new EntityFolderPort(services.GetRequiredService<DbContext>()));
        services.AddScoped<IEntitySceneIdentityPort>(services => new EntitySceneIdentityPort(
            services.GetRequiredService<DbContext>(),
            services.GetRequiredService<OptionsStore>()));

        // A singleton, so the request scopes and the background worker queue behind one gate. Per
        // scope it would be a gate per request and would serialise nothing.
        services.AddSingleton(_ => new OptionsWriteGate(_log));

        // A singleton for the same reason: the window it closes spans two requests.
        services.AddSingleton(_ => new RegistrationGate());

        // A delivery arrives per file, so a reading held per scope would be a reading taken per file.
        services.AddSingleton(services => new ReportedRootCache(services.GetRequiredService<TimeProvider>()));

        // A singleton for a different reason: the count is taken by a background run and read by a
        // later request, so a slot held per scope would be a slot the reader never sees.
        services.AddSingleton(services => new SyncPreviewCache(services.GetRequiredService<TimeProvider>()));
        services.AddScoped<IImportPathPort, ImportPathPort>();
        services.AddScoped<IReportedRootPort>(services => new ReportedRootPort(
            services.GetRequiredService<IWhisparrClient>(),
            services.GetRequiredService<OptionsStore>(),
            services.GetRequiredService<ICredentialPort>(),
            services.GetRequiredService<ReportedRootCache>(),
            _log));

        // Both host-supplied dependencies are optional, matching how the configuration is already
        // taken: a container that cannot produce one still builds the port, and the ingest reports
        // the refusal instead of failing to resolve.
        services.AddScoped<ICoveLibraryPort>(services => new CoveLibraryPort(
            services.GetRequiredService<DbContext>(),
            services.GetService<IScanService>(),
            services.GetService<IMetadataServerService>(),
            _coveConfig,
            _log));
        // The pending batch is what makes a burst of deliveries one scan instead of one each, and a
        // batch held per scope would be a batch per delivery.
        services.AddSingleton(services => new FollowUpScanCoalescer(
            services.GetRequiredService<TimeProvider>(), _log));
        services.AddScoped<IBackstopPass>(services => new BackstopPass(
            services.GetRequiredService<IWhisparrClient>(),
            services.GetRequiredService<OptionsStore>(),
            services.GetRequiredService<OptionsWriteGate>(),
            services.GetRequiredService<ICredentialPort>(),
            services.GetRequiredService<IImportCore>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<FollowUpScanCoalescer>(),
            services.GetRequiredService<ICoveLibraryPort>(),
            _log));
        services.AddScoped<IImportCore>(services => new ImportCore(
            services.GetRequiredService<IReportedRootPort>(),
            services.GetRequiredService<ICoveLibraryPort>(),
            services.GetRequiredService<IImportPathPort>(),
            services.GetRequiredService<OptionsStore>(),
            services.GetRequiredService<OptionsWriteGate>(),
            services.GetRequiredService<FollowUpScanCoalescer>(),
            services.GetRequiredService<TimeProvider>(),
            _log));

        services.AddMissingProviders();
        services.AddMissingDerivation();
        services.AddLibraryStatus(_log);
        services.AddFolderAddressing(_log);
    }

    public override async Task InitializeAsync(
        IServiceProvider services, CancellationToken ct = default)
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

        await ReadStoredGenerationAsync(services, ct).ConfigureAwait(false);

        await base.InitializeAsync(services, ct).ConfigureAwait(false);
    }

    // Registered as a factory over this instance because the host hands an extension its store
    // through IStatefulExtension.SetStore and registers it in no container. Each load publishes
    // the generation it established, which also covers the host's own extension-data route: the
    // manifest is built synchronously on a host thread and can only read what a load published.
    internal OptionsStore NewOptionsStore()
        => new(Store, _log, generation => _selectedGeneration = generation);

    // Reads the store once at load, so the manifest has a generation to register for. The load
    // publishes what it established, so nothing is assigned here. A store that could not be read
    // is reported once and leaves the generation unestablished, so the extension still loads.
    // Resolved inside a scope for the reason CanObtain records.
    private async Task ReadStoredGenerationAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateScope();
            await scope.ServiceProvider
                .GetRequiredService<OptionsStore>()
                .LoadAsync(ct)
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Load-time read: an unreadable store is the answer, not a fault.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogNoStoredGeneration();
        }
    }

    // Resolved inside a scope rather than off the provider directly. The host copies its own scoped
    // registrations into the extension container and builds it with scope validation on, so
    // resolving one of them from the provider handed to InitializeAsync throws, and a throw there
    // disables the extension. The instance is discarded because a scoped one kept in a field
    // outlives its scope, and a construction failure is the answer the caller asked for.
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
