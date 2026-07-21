using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// The version gate on the UI manifest. v2 (Sonarr) scenes are episodes with no StashDB identity and v2 has no
/// performer entity: the per-scene / library-video / performer surfaces have no v2 meaning and, being host-drawn
/// from the manifest, must be omitted on v2. The version-neutral surfaces (settings tab, studio/performer monitor
/// slots) and the STUDIO library surfaces (a v2 studio monitors as a site) stay on v2.
/// </summary>
public sealed class ManifestVersionGateTests
{
    private const string SceneTabComponent = "WhisparrScenePanel";
    private const string BulkActionId = "whisparr-batch-video";

    // The slots that must NOT appear on v2 — scene (videos) surfaces and every performer surface.
    private static readonly string[] V2OmittedSlots =
    [
        "videos-list-toolbar-end", "performers-list-toolbar-end",
        "videos-list-row", "performers-list-row",
        "video-card-content", "performer-card-footer",
    ];

    // The studio library surfaces that DO appear on v2 (a v2 studio monitors as a site, batched by TPDB).
    private static readonly string[] V2StudioSlots =
    [
        "studios-list-toolbar-end", "studios-list-row", "studio-card-footer",
    ];

    [Theory]
    [InlineData("v3")]
    [InlineData(null)] // unconfigured shows the per-scene surfaces; only an explicit v2 hides them
    public void PerSceneSurfaces_PresentWhenNotV2(string? version)
    {
        var manifest = new Ext().BuildManifest(version);

        Assert.Contains(manifest.Tabs, t => t.ComponentName == SceneTabComponent);
        Assert.Contains(manifest.Slots, s => s.Slot == "videos-list-toolbar-end");
        Assert.Contains(manifest.Actions, a => a.Id == BulkActionId);
    }

    [Fact]
    public void SceneAndPerformerSurfaces_OmittedOnV2()
    {
        var manifest = new Ext().BuildManifest("v2");

        Assert.DoesNotContain(manifest.Tabs, t => t.ComponentName == SceneTabComponent);
        Assert.DoesNotContain(manifest.Actions, a => a.Id == BulkActionId);
        foreach (var slot in V2OmittedSlots)
        {
            Assert.DoesNotContain(manifest.Slots, s => s.Slot == slot);
        }
    }

    [Fact]
    public void StudioAndVersionNeutralSurfaces_PresentOnV2()
    {
        var manifest = new Ext().BuildManifest("v2");

        Assert.Contains(manifest.Slots, s => s.ComponentName == "WhisparrMonitorButton");
        Assert.Contains(manifest.SettingsTabs, t => t.Key == "whisparr-sync");
        foreach (var slot in V2StudioSlots)
        {
            Assert.Contains(manifest.Slots, s => s.Slot == slot);
        }
    }
}
