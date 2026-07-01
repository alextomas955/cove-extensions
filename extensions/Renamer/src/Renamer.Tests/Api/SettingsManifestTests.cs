using Cove.Core.Auth;

namespace Renamer.Tests;

/// <summary>
/// UI-03 (automated portion): the extension registers its UI so Cove loads it. The renamer UI's home is
/// a DEDICATED SETTINGS TAB (Settings → Extensions → Renamer): GetUIManifest() declares a settings tab
/// "renamer" plus a settings section targeting it that renders the RenamerPage component, alongside the
/// bulk action. It is NOT a top-nav page and NOT under the shared Installed list. The live half (the
/// tab actually renders) is verified by the live browser pass.
/// </summary>
public sealed class SettingsManifestTests
{
    private static global::Renamer.Renamer NewExtension() => new();

    [Fact]
    public void GetUIManifest_DeclaresJsBundleUrl_ForTheHostToLoadThePanel()
    {
        var manifest = NewExtension().GetUIManifest();

        // A non-null bundle URL is what wires the UI into Cove.
        Assert.Equal("index.mjs", manifest.JsBundleUrl);
    }

    [Fact]
    public void GetUIManifest_DeclaresADedicatedRenamerSettingsTab()
    {
        var manifest = NewExtension().GetUIManifest();

        // Own first-class Settings tab (under the Extensions settings group), not the crowded
        // Installed list and not the top nav bar.
        var tab = Assert.Single(manifest.SettingsTabs);
        Assert.Equal("renamer", tab.Key);
        Assert.Equal("Renamer", tab.Label);
        Assert.Equal("com.alextomas955.renamer", tab.ExtensionId);
    }

    [Fact]
    public void GetUIManifest_RendersRenamerPageInsideTheRenamerTab()
    {
        var manifest = NewExtension().GetUIManifest();

        // The section targets the "renamer" tab and renders RenamerPage — the host's
        // getSettingsPanelsForTab("renamer") returns this panel and mounts the component inside the tab.
        var panel = Assert.Single(manifest.SettingsPanels);
        Assert.Equal("renamer", panel.TargetTab);
        // Key link: this literal MUST match the bundle's defineExtension components map key (RenamerPage).
        Assert.Equal("RenamerPage", panel.ComponentName);
    }

    [Fact]
    public void GetUIManifest_StillContributesTheRenamerSelectedBulkAction()
    {
        var manifest = NewExtension().GetUIManifest();

        // The bulk action is unaffected by the home change — it dispatches the renamerSelected JS handler
        // (no ApiEndpoint) for the in-context confirm/undo flow. It is registered once per kind (video,
        // image) so each carries its matching write permission.
        Assert.Equal(2, manifest.Actions.Count);
        foreach (var action in manifest.Actions)
        {
            Assert.Equal("bulk", action.ActionType);
            Assert.Equal("renamerSelected", action.HandlerName);
            Assert.Null(action.ApiEndpoint);
        }

        var video = Assert.Single(manifest.Actions, a => a.Id == "renamer-selected-video");
        Assert.Equal(Permissions.VideosWrite, video.RequiredPermission);
        var image = Assert.Single(manifest.Actions, a => a.Id == "renamer-selected-image");
        Assert.Equal(Permissions.ImagesWrite, image.RequiredPermission);
    }

    [Fact]
    public void GetUIManifest_HasNoTopNavPage_HomeIsTheSettingsTab()
    {
        var manifest = NewExtension().GetUIManifest();

        // The renamer UI moved from a top-nav AddPage to the Settings tab; assert no page lingers.
        Assert.Empty(manifest.Pages);
    }
}
