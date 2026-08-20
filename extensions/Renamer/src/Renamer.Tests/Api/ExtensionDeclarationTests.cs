using Cove.Core.Auth;
using Cove.Plugins;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests;

/// <summary>
/// Everything the extension declares to the host, asserted against the exact contributed shape the
/// host renders and dispatches against: the JS bundle, the dedicated settings tab and the panel inside
/// it, the absence of a top-nav page, the per-kind bulk actions, and the registered job.
/// </summary>
/// <remarks>
/// UI-03 (automated portion): the renamer UI's home is a DEDICATED SETTINGS TAB (Settings →
/// Extensions → Renamer), not the shared Installed list and not the top nav. The live half (the tab
/// actually renders) is verified by the live browser pass.
/// <para>
/// The bulk action is contributed through <c>GetUIManifest()</c> and NOT <c>GetActions()</c> —
/// <c>FullExtensionBase</c> does not implement <c>IActionExtension</c> — while the
/// <c>renamer-batch</c> job is registered via <c>DefineJobs()</c>.
/// </para>
/// </remarks>
[Trait("Tier", "L1")]
public sealed class ExtensionDeclarationTests
{
    private static global::Renamer.Renamer NewExtension() => RenamerFixture.Create();

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
    public void GetUIManifest_HasNoTopNavPage_HomeIsTheSettingsTab()
    {
        var manifest = NewExtension().GetUIManifest();

        // The renamer UI moved from a top-nav AddPage to the Settings tab; assert no page lingers.
        Assert.Empty(manifest.Pages);
    }

    [Fact]
    public void GetUIManifest_ContributesPerKindBulkActions_EachWithItsMatchingPermission()
    {
        var ext = NewExtension();

        var manifest = ext.GetUIManifest();

        // The bulk action is registered ONCE PER KIND (video, image) so each carries the matching
        // RequiredPermission — the host's action model allows only a single permission per action and
        // filters visibility by both entity-type context AND that permission.
        Assert.Equal(2, manifest.Actions.Count);

        var video = Assert.Single(manifest.Actions, a => a.Id == "renamer-selected-video");
        Assert.Equal("Rename selected", video.Label);
        Assert.Equal("com.alextomas955.renamer", video.ExtensionId);
        Assert.Equal("bulk", video.ActionType);
        Assert.Equal(["video"], video.EntityTypes);
        // The action dispatches the JS handler instead of POSTing directly, so the host can gate
        // execution behind a preview → confirm. HandlerName is set; no ApiEndpoint.
        Assert.Equal("renamerSelected", video.HandlerName);
        Assert.Null(video.ApiEndpoint);
        Assert.Equal(Permissions.VideosWrite, video.RequiredPermission);

        var image = Assert.Single(manifest.Actions, a => a.Id == "renamer-selected-image");
        Assert.Equal("Rename selected", image.Label);
        Assert.Equal(["image"], image.EntityTypes);
        Assert.Equal("renamerSelected", image.HandlerName);
        Assert.Null(image.ApiEndpoint);
        Assert.Equal(Permissions.ImagesWrite, image.RequiredPermission);
    }

    // Kept alongside the per-kind case above rather than folded into it: the two were assert-diffed
    // when this file was merged, and the loop below asserts ActionType over EVERY contributed action,
    // whereas the per-kind case names only the video one. So `image.ActionType` is pinned here and
    // nowhere else, and the resemblance between the two is not containment.
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
    public void Jobs_RegistersTheRenamerBatchDefinition()
    {
        var ext = NewExtension();

        var job = Assert.Single(((IJobExtension)ext).Jobs);
        Assert.Equal("renamer-batch", job.Id);
        Assert.True(job.SupportsParameters);
        Assert.True(job.ShowInTaskList);
    }
}
