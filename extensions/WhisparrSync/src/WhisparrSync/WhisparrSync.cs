using Cove.Core.Interfaces;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhisparrSync.Client;
using WhisparrSync.Options;
using WhisparrSync.Reconcile;

namespace WhisparrSync;

/// <summary>
/// The Whisparr Sync extension entry point: a settings-only <see cref="FullExtensionBase"/> that
/// connects Cove to a user-configured Whisparr v3 instance. The Cove-facing surface (settings-page
/// manifest + minimal-API endpoints) and the typed-client registration live in the sibling
/// <c>WhisparrSync.Api.cs</c> / <c>WhisparrSync.Logging.cs</c> partials.
/// </summary>
public sealed partial class WhisparrSync : FullExtensionBase, IDisposable
{
    public override string Id => "com.alextomas955.whisparrsync";
    public override string Name => "Whisparr Sync";

    // Repo-committed dev placeholder, not release-stamped: the published artifact's real version comes
    // from the release tag (build.yml -p:Version= + the packaged extension.json/package.json stamps).
    public override string Version => "0.1.0";
    public override string? Description =>
        "Connects Cove to a Whisparr v3 instance; the API key is stored server-side only and never echoed.";
    public override string? Author => "alextomas955";
    public override string? Url => "https://github.com/alextomas955/cove-extensions";
    public override IReadOnlyList<string> Categories => [ExtensionCategories.Tools, ExtensionCategories.Automation];

    // Baselines on the Cove 1.0.0 GA release. (The host APIs it binds to — the metadata-server
    // client (IMetadataServerService), the card / list-row slots, and page-layout settings — shipped in 0.9.1.)
    public override string? MinCoveVersion => "1.0.0";

    /// <summary>
    /// The host logger, non-null by construction: defaults to a no-op logger and is replaced in
    /// <see cref="InitializeAsync"/> when the host supplies one, so the source-generated
    /// <c>[LoggerMessage]</c> methods in <c>WhisparrSync.Logging.cs</c> never dereference null and a
    /// missing host logger never blocks a connection test. (The generator binds this field by its
    /// <see cref="ILogger"/> type.) It never logs the API key or a URL-with-key.
    /// </summary>
    private ILogger _log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>
    /// The host scope factory, captured in <see cref="InitializeAsync"/>. A reconciliation opens a fresh
    /// <c>CreateAsyncScope()</c> per run and resolves the scoped <c>DbContext</c> from it to construct the
    /// <c>CoveLibraryPort</c> — the correct lifetime for a scoped context (never a long-lived captured one).
    /// </summary>
    private IServiceScopeFactory? _scopeFactory;

    private IServiceScopeFactory ScopeFactory => _scopeFactory
        ?? throw new InvalidOperationException("ScopeFactory used before InitializeAsync captured IServiceScopeFactory");

    /// <summary>
    /// The host job queue, captured (optional — null-guarded like the logger) in
    /// <see cref="InitializeAsync"/>. The self-scheduled reconcile loop enqueues an exclusive reconcile job
    /// through it each tick; a host that supplies none simply runs webhook-only until restart.
    /// </summary>
    private IJobService? _jobs;

    // Cancels the fire-and-forget reconcile loop on ShutdownAsync. Nulled/created once so InitializeAsync is
    // idempotent (a re-init never starts a second loop).
    private CancellationTokenSource? _reconcileCts;

    // The connected Whisparr version ("v2"/"v3"), cached for the SYNC GetUIManifest gate (the host rebuilds the
    // manifest per UI fetch; a version switch reflects on the next reload). Set on init and on options-save.
    // v2 has no per-scene StashDB identity: the per-scene / library-video surfaces (scene tab, videos-list
    // status toolbar, "Whisparr" bulk action) are omitted from the manifest when this is "v2".
    private volatile string? _selectedVersion;

    // The reconcile cadence. Fixed at 15 min; not surfaced as a UI setting, so no options field /
    // doc row is added for it yet.
    private const int DefaultReconcileIntervalMinutes = 15;

    /// <summary>
    /// Registers the typed <see cref="WhisparrClient"/> on the host's pooled <c>IHttpClientFactory</c>
    /// (the overlay's <c>AddHttpClient()</c> supports an extension's own <c>AddHttpClient&lt;T&gt;()</c>).
    /// The client is resolved per request in the settings endpoints; nothing here is copy-local/bundled.
    /// </summary>
    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
        => services.AddHttpClient<WhisparrClient>();

    public override async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        // Logging is optional: keep the NullLogger default when the host supplies none (GetService, not
        // GetRequiredService) so a settings-only extension still loads.
        _log = services.GetService<ILogger<WhisparrSync>>() ?? _log;
        _scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        _jobs = services.GetService<IJobService>();
        await base.InitializeAsync(services, ct);
        _selectedVersion = (await new OptionsStore(Store).LoadAsync(ct)).SelectedVersion;
        StartReconcileLoop();
    }

    /// <summary>Cancels the reconcile loop so the host can unload the extension cleanly.</summary>
    public override Task ShutdownAsync(CancellationToken ct = default)
    {
        _reconcileCts?.Cancel();
        return base.ShutdownAsync(ct);
    }

    /// <summary>Cancels and releases the reconcile loop's cancellation source (idempotent).</summary>
    public void Dispose()
    {
        _reconcileCts?.Cancel();
        _reconcileCts?.Dispose();
        _reconcileCts = null;
    }

    // Start the self-scheduled reconcile loop fire-and-forget. Idempotent (a second Initialize is a
    // no-op). Guarded so a loop fault never propagates out of Initialize and never crashes the host.
    private void StartReconcileLoop()
    {
        if (_reconcileCts is not null)
        {
            return;
        }

        _reconcileCts = new CancellationTokenSource();
        _ = RunReconcileLoopAsync(_reconcileCts.Token);
    }

    private async Task RunReconcileLoopAsync(CancellationToken ct)
    {
        try
        {
            var scheduler = new ReconcileScheduler(
                _jobs, (_, c) => RunReconcileAsync(c), TimeSpan.FromMinutes(DefaultReconcileIntervalMinutes));
            await scheduler.RunLoopAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — the CTS was cancelled in ShutdownAsync.
        }
        catch (Exception ex)
        {
            LogReconcileLoopFault(ex.Message); // never silent, never rethrown out of the fire-and-forget loop
        }
    }
}
