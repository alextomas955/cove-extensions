using Renamer.Tests.TestSupport;

namespace Renamer.Tests;

public sealed class SettingsManifestTests
{
    private static global::Renamer.Renamer NewExtension() => RenamerFixture.Create();

    [Fact]
    public void GetUIManifest_DeclaresJsBundleUrl_ForTheHostToLoadThePanel()
    {
        var manifest = NewExtension().GetUIManifest();

        Assert.Equal("index.mjs", manifest.JsBundleUrl);
    }

    [Fact]
    public void GetUIManifest_DeclaresADedicatedRenamerSettingsTab()
    {
        var manifest = NewExtension().GetUIManifest();

        var tab = Assert.Single(manifest.SettingsTabs);
        Assert.Equal("renamer", tab.Key);
        Assert.Equal("Renamer", tab.Label);
        Assert.Equal("com.alextomas955.renamer", tab.ExtensionId);
    }

    [Fact]
    public void GetUIManifest_RendersRenamerPageInsideTheRenamerTab()
    {
        var manifest = NewExtension().GetUIManifest();

        var panel = Assert.Single(manifest.SettingsPanels);
        Assert.Equal("renamer", panel.TargetTab);
        // This literal must match the bundle's defineExtension components map key, or the host mounts
        // nothing and reports nothing.
        Assert.Equal("RenamerPage", panel.ComponentName);
    }

    [Fact]
    public void GetUIManifest_HasNoTopNavPage_HomeIsTheSettingsTab()
    {
        var manifest = NewExtension().GetUIManifest();

        Assert.Empty(manifest.Pages);
    }
}
