using Cove.Plugins;
using Cove.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhisparrSync.Client;

namespace WhisparrSync;

/// <summary>
/// The Whisparr Sync extension entry point: a settings-only <see cref="FullExtensionBase"/> that
/// connects Cove to a user-configured Whisparr v3 instance. The Cove-facing surface (settings-page
/// manifest + minimal-API endpoints) and the typed-client registration live in the sibling
/// <c>WhisparrSync.Api.cs</c> / <c>WhisparrSync.Logging.cs</c> partials.
/// </summary>
public sealed partial class WhisparrSync : FullExtensionBase
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

    // Page-layout settings tab (SettingsTabLayout.Page) is a 0.9.0 host capability — matches Renamer's floor.
    public override string? MinCoveVersion => "0.9.0";

    /// <summary>
    /// The host logger, non-null by construction: defaults to a no-op logger and is replaced in
    /// <see cref="InitializeAsync"/> when the host supplies one, so the source-generated
    /// <c>[LoggerMessage]</c> methods in <c>WhisparrSync.Logging.cs</c> never dereference null and a
    /// missing host logger never blocks a connection test. (The generator binds this field by its
    /// <see cref="ILogger"/> type.) It never logs the API key or a URL-with-key (CONN-06).
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
    /// Registers the typed <see cref="WhisparrClient"/> on the host's pooled <c>IHttpClientFactory</c>
    /// (the overlay's <c>AddHttpClient()</c> supports an extension's own <c>AddHttpClient&lt;T&gt;()</c>).
    /// The client is resolved per request in the settings endpoints; nothing here is copy-local/bundled.
    /// </summary>
    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
        => services.AddHttpClient<WhisparrClient>();

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        // Logging is optional: keep the NullLogger default when the host supplies none (GetService, not
        // GetRequiredService) so a settings-only extension still loads.
        _log = services.GetService<ILogger<WhisparrSync>>() ?? _log;
        _scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        return base.InitializeAsync(services, ct);
    }
}
