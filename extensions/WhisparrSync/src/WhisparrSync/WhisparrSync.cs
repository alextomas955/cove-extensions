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

    /// <summary>
    /// The host logger, writing to Cove's normal log. Non-null by construction: it defaults to a no-op
    /// logger and is replaced in <see cref="InitializeAsync"/> if the host supplies one, so the
    /// source-generated <c>[LoggerMessage]</c> methods never dereference null. (The generator binds to
    /// this field by its <see cref="ILogger"/> type.)
    /// </summary>
    private ILogger _log = NullLogger.Instance;

    /// <summary>
    /// The stored generation's enum name, or null while none is established.
    /// </summary>
    /// <remarks>
    /// Volatile because <see cref="GetUIManifest"/> is called on host threads other than the one a
    /// settings save answers on, and a name because <c>volatile</c> accepts a reference type and no
    /// nullable enum. Null is a generation not established, which registers every surface: a load
    /// that could not be made must not remove one.
    /// </remarks>
    private volatile string? _selectedGeneration;

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

        // One registration of the generated client per address-and-key pair, held for the life of the
        // extension: the pairs come from settings a person edits, and a registration per request
        // would be a handler pool per request.
        services.AddSingleton<Whisparr3Gateway>();
        services.AddSingleton<Whisparr2Gateway>();

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

        // A singleton, so the request scopes and the background worker queue behind ONE gate. Per
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

    /// <summary>This extension's options store, publishing the generation each load establishes.</summary>
    /// <remarks>
    /// The manifest is built synchronously on a host thread, so it cannot load the store and reads
    /// what the last load published instead. Publishing from the load rather than from this
    /// extension's own save covers every writer of the blob, including the host's own
    /// extension-data route, which reaches no code of this extension at all.
    /// <para>
    /// A factory over this instance rather than a type registration: the host hands an extension its
    /// store through <c>IStatefulExtension.SetStore</c> and registers it in no container.
    /// </para>
    /// </remarks>
    internal OptionsStore NewOptionsStore()
        => new(Store, _log, generation => _selectedGeneration = generation);

    /// <summary>Reads the store once at load, so the manifest has a generation to register for.</summary>
    /// <remarks>
    /// The load itself publishes what it established, so nothing is assigned here and there is one
    /// writer rather than two agreeing by hand. A store that could not be read is reported once and
    /// leaves the generation unestablished, so the extension still loads and keeps every surface.
    /// <para>
    /// Resolved inside a scope for the reason <see cref="CanObtain{T}"/> records.
    /// </para>
    /// </remarks>
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
