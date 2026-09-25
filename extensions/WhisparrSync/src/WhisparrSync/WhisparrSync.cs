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
using WhisparrSync.Jobs;
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

        // The instance's own lookup, so a studio held under a provider's code costs no second
        // request. Holds nothing between calls and takes the instance per call.
        services.AddSingleton<ISiteNumberPort>(
            provider => new InstanceSiteNumberPort(
                provider.GetRequiredService<Whisparr2Gateway>()));

        // The host stands the AddHttpClient stack up before this call.
        services.AddHttpClient<WhisparrTransport>(WhisparrTransport.Configure)
            .ConfigurePrimaryHttpMessageHandler(WhisparrTransport.CreateHandler)
            .AddTypedClient((http, provider) => new WhisparrTransport(
                http, provider.GetRequiredService<Whisparr3Gateway>(), _log));

        // Which generation is connected is a stored setting read per request, so an instance
        // registered here would predate the connection it describes.
        services.AddTransient<IWhisparrInstanceFactory>(services => new WhisparrInstanceFactory(
            services.GetRequiredService<WhisparrTransport>(),
            services.GetRequiredService<Whisparr3Gateway>(),
            services.GetRequiredService<Whisparr2Gateway>(),
            services.GetRequiredService<ISiteNumberPort>(),
            _log));

        services.AddSingleton(TimeProvider.System);

        services.AddScoped(services => new WhisparrAccess(
            services.GetRequiredService<OptionsStore>(),
            services.GetRequiredService<ICredentialPort>(),
            services.GetRequiredService<IWhisparrInstanceFactory>(),
            _log));
        services.AddScoped(services => new BackgroundWork(
            services.GetRequiredService<IJobService>(),
            services.GetRequiredService<IServiceScopeFactory>()));
        services.AddScoped(services => new OptionsWriting(
            services.GetRequiredService<OptionsStore>(),
            services.GetRequiredService<OptionsWriteGate>()));
        services.AddScoped(services => new CallbackAddressing(
            services.GetRequiredService<OptionsStore>(),
            services.GetRequiredService<ICallbackSecretPort>(),
            services.GetRequiredService<IHostLockdownPort>(),
            services.GetRequiredService<TimeProvider>()));
        services.AddScoped(services => new CallbackRegistering(
            services.GetRequiredService<OptionsWriteGate>(),
            services.GetRequiredService<ICredentialPort>(),
            services.GetRequiredService<IWhisparrNotificationPort>(),
            services.GetRequiredService<RegistrationGate>()));
        services.AddScoped<IWhisparrConnectionTester, ConnectionTester>();
        services.AddScoped<IConnectionTestRunner, ConnectionTestRunner>();
        services.AddScoped<ICredentialPort, CredentialPort>();
        services.AddScoped<IHostLockdownPort>(services => new HostLockdownPort(
            services.GetService<CoveConfiguration>(), services.GetService<IUserService>()));
        services.AddScoped<ICallbackSecretPort>(
            services => new CallbackSecretPort(services.GetRequiredService<DbContext>(), _log));
        services.AddScoped<IWhisparrNotificationPort>(services => new NotificationPort(
            services.GetRequiredService<IWhisparrInstanceFactory>(), _log));
        services.AddScoped(_ => NewOptionsStore());
        services.AddScoped<IEntityIdentityPort>(services => new EntityIdentityPort(
            services.GetRequiredService<DbContext>(),
            services.GetRequiredService<OptionsStore>()));
        services.AddScoped<IEntityFolderPort>(
            services => new EntityFolderPort(services.GetRequiredService<DbContext>()));
        services.AddScoped<IEntitySceneIdentityPort>(services => new EntitySceneIdentityPort(
            services.GetRequiredService<DbContext>(),
            services.GetRequiredService<OptionsStore>()));

        // Singletons because each holds state spanning more than one request. Per scope the write
        // gate would serialise nothing, the registration gate would not close a window spanning two
        // requests, the root cache would take a reading per delivered file, and the preview slot
        // would be filled by a background run the later reader never sees.
        services.AddSingleton(_ => new OptionsWriteGate(_log));
        services.AddSingleton(_ => new RegistrationGate());
        services.AddSingleton(services => new ReportedRootCache(services.GetRequiredService<TimeProvider>()));
        services.AddSingleton(services => new SyncPreviewCache(services.GetRequiredService<TimeProvider>()));
        services.AddScoped<IImportPathPort, ImportPathPort>();
        services.AddScoped<IReportedRootPort>(services => new ReportedRootPort(
            services.GetRequiredService<IWhisparrInstanceFactory>(),
            services.GetRequiredService<ICredentialPort>(),
            services.GetRequiredService<ReportedRootCache>(),
            _log));

        // Both host services are optional: a container that cannot produce one still builds the
        // port, and the ingest reports the refusal instead of failing to resolve.
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
            services.GetRequiredService<WhisparrAccess>(),
            services.GetRequiredService<OptionsWriteGate>(),
            services.GetRequiredService<IImportCore>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<FollowUpScanCoalescer>(),
            services.GetRequiredService<ICoveLibraryPort>()));
        services.AddScoped<IImportCore>(services => new ImportCore(
            services.GetRequiredService<IReportedRootPort>(),
            services.GetRequiredService<ICoveLibraryPort>(),
            services.GetRequiredService<IImportPathPort>(),
            services.GetRequiredService<OptionsWriting>(),
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
        // Both are taken with GetService: a host that forwards neither must still load the
        // extension, and what is missing is reported rather than thrown on.
        _log = services.GetService<ILogger<WhisparrSync>>() ?? _log;
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

    // A factory over this instance: the host hands an extension its store through
    // IStatefulExtension.SetStore and registers it in no container. Each load publishes the
    // generation it established, which the manifest reads; it is built synchronously on a host
    // thread and can read nothing else.
    internal OptionsStore NewOptionsStore()
        => new(Store, _log, generation => _selectedGeneration = generation);

    // Reads the store once at load, so the manifest has a generation to register for. A store
    // that could not be read leaves the generation unestablished and the extension still loads.
    // Scoped for the reason CanObtain records.
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

    // The host copies its own scoped registrations into the extension container and builds it
    // with scope validation on, so resolving one from the provider handed to InitializeAsync throws
    // and the throw disables the extension. The instance is discarded: a scoped one kept in a field
    // outlives its scope.
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
