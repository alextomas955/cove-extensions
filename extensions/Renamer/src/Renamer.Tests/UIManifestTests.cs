using Cove.Core.Auth;
using Cove.Plugins;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests;

public sealed class UIManifestTests
{
    private static UIManifest Manifest() => RenamerFixture.Create().GetUIManifest();

    [Fact]
    public void GetUIManifest_ContributesPerKindBulkActions_EachWithItsMatchingPermission()
    {
        var manifest = Manifest();

        // The bulk action is registered once per kind (video, image, text) so each carries the matching
        // RequiredPermission - the host's action model allows only a single permission per action and
        // filters visibility by both entity-type context and that permission.
        Assert.Equal(3, manifest.Actions.Count);

        var video = Assert.Single(manifest.Actions, a => a.Id == "renamer-selected-video");
        Assert.Equal("Rename selected", video.Label);
        Assert.Equal("com.alextomas955.renamer", video.ExtensionId);
        Assert.Equal("bulk", video.ActionType);
        Assert.Equal(["video"], video.EntityTypes);
        // The action dispatches the JS handler instead of POSTing directly, so the host can gate
        // execution behind a preview and a confirm.
        Assert.Equal("renamerSelected", video.HandlerName);
        Assert.Null(video.ApiEndpoint);
        Assert.Equal(Permissions.VideosWrite, video.RequiredPermission);

        var image = Assert.Single(manifest.Actions, a => a.Id == "renamer-selected-image");
        Assert.Equal("Rename selected", image.Label);
        Assert.Equal(["image"], image.EntityTypes);
        Assert.Equal("renamerSelected", image.HandlerName);
        Assert.Null(image.ApiEndpoint);
        Assert.Equal(Permissions.ImagesWrite, image.RequiredPermission);

        var text = Assert.Single(manifest.Actions, a => a.Id == "renamer-selected-text");
        Assert.Equal("Rename selected", text.Label);
        // Both spellings: the host singularizes only "videos" and "images" before matching an action's
        // entity types, so a texts list hands it the plural.
        Assert.Equal(["text", "texts"], text.EntityTypes);
        Assert.Equal("renamerSelected", text.HandlerName);
        Assert.Null(text.ApiEndpoint);
        Assert.Equal(Permissions.TextsWrite, text.RequiredPermission);
    }

    // The rename reports into the host's job drawer, so the host's native "queued" alert would be a
    // second, blocking notice of the same thing.
    [Fact]
    public void GetUIManifest_EveryBulkAction_SuppressesTheHostsSuccessAlert()
    {
        Assert.All(Manifest().Actions, action => Assert.True(action.SuppressSuccessAlert, action.Id));
    }

    [Fact]
    public void Jobs_RegistersNone_SoOnlyTheCheckedEndpointsCanStartARename()
    {
        Assert.Empty(((IJobExtension)RenamerFixture.Create()).Jobs);
    }

    [Fact]
    public void GetUIManifest_DeclaresJsBundleUrl_ForTheHostToLoadThePanel()
    {
        Assert.Equal("index.mjs", Manifest().JsBundleUrl);
    }

    [Fact]
    public void GetUIManifest_DeclaresADedicatedRenamerSettingsTab_RenderedAsAPage()
    {
        var tab = Assert.Single(Manifest().SettingsTabs);
        Assert.Equal("renamer", tab.Key);
        Assert.Equal("Renamer", tab.Label);
        Assert.Equal("com.alextomas955.renamer", tab.ExtensionId);
        // Page layout renders the panel full-width with no card chrome; the default stacks it in a card.
        Assert.Equal(SettingsTabLayout.Page, tab.Layout);
    }

    [Fact]
    public void GetUIManifest_RendersRenamerPageInsideTheRenamerTab()
    {
        var panel = Assert.Single(Manifest().SettingsPanels);
        Assert.Equal("renamer", panel.TargetTab);
        // This literal must match the bundle's defineExtension components map key, or the host mounts
        // nothing and reports nothing.
        Assert.Equal("RenamerPage", panel.ComponentName);
    }

    [Fact]
    public void GetUIManifest_HasNoTopNavPage_HomeIsTheSettingsTab()
    {
        Assert.Empty(Manifest().Pages);
    }
}
