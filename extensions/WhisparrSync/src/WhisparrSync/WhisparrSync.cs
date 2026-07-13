using Cove.Plugins;
using Cove.Sdk;

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
}
