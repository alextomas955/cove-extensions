using Cove.Plugins;
using ScanAndIdentify.Tests.TestSupport;

namespace ScanAndIdentify.Tests;

public sealed class UIManifestTests
{
    private static UIManifest Manifest() => ScanAndIdentifyFixture.Create().GetUIManifest();

    [Fact]
    public void GetUIManifest_DeclaresOneSettingsTab_RenderedAsStackedPanels()
    {
        var tab = Assert.Single(Manifest().SettingsTabs);

        Assert.Equal("scan-and-identify", tab.Key);
        Assert.Equal("Scan and Identify", tab.Label);
        Assert.Equal("com.alextomas955.scanandidentify", tab.ExtensionId);
        Assert.Equal(SettingsTabLayout.Panels, tab.Layout);
    }

    [Fact]
    public void GetUIManifest_RendersScanAndIdentifyPageInsideThatTab()
    {
        var panel = Assert.Single(Manifest().SettingsPanels);

        Assert.Equal("scan-and-identify", panel.TargetTab);
        // This literal must match the bundle's defineExtension components map key, or the host mounts
        // nothing and reports nothing.
        Assert.Equal("ScanAndIdentifyPage", panel.ComponentName);
    }

    [Fact]
    public void GetUIManifest_DeclaresJsBundleUrl_ForTheHostToLoadThePanel()
    {
        Assert.Equal("index.mjs", Manifest().JsBundleUrl);
    }
}
