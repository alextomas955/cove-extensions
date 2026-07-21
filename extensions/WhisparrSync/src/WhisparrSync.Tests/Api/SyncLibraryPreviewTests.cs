using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Options;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api;

// Bare-CI pure-unit tier: references only WhisparrSync DTOs + the pure adapter capability flags, never a Cove
// type. Covers Ext.SyncPreviewCore — the "Sync my library to Whisparr" preview counting.
public sealed class SyncLibraryPreviewTests
{
    private static IWhisparrAdapter Adapter(string version)
        => AdapterSelector.SelectForVersion(version, new WhisparrClient(new HttpClient()))!;

    private static CoveEntityRef Studio(int id, string? stashId, string? tpdbId)
        => new(id, stashId is null ? [] : [stashId], tpdbId is null ? [] : [tpdbId]);

    private static CoveVideo Scene(int id, string? stashId)
        => new(id, $"Scene {id}", null, stashId is null ? [] : [stashId], [], [], []);

    [Fact]
    public void SyncPreviewCore_V3_SplitsStudiosPerformersScenes_ByStashDbId()
    {
        var studios = new[] { Studio(1, "s-1", null), Studio(2, "s-2", null), Studio(3, null, null) };
        var performers = new[] { Studio(10, "p-1", null), Studio(11, null, null) };
        var videos = new[] { Scene(100, "v-1"), Scene(101, "v-2"), Scene(102, null) };

        var result = Ext.SyncPreviewCore(
            studios, performers, videos, new WhisparrOptions { SelectedVersion = "v3" }, Adapter("v3"));

        Assert.Equal(2, result.Studios.WithId);
        Assert.Equal(1, result.Studios.Skipped);
        Assert.Equal(1, result.Performers.WithId);
        Assert.Equal(1, result.Performers.Skipped);
        Assert.Equal(2, result.Scenes.WithId);
        Assert.Equal(1, result.Scenes.Skipped);
    }

    [Fact]
    public void SyncPreviewCore_V2_CountsStudiosByTpdbId_AndZeroesPerformersAndScenes()
    {
        // v2 studios resolve by ThePornDB id; the StashDB ids present on the performers/scenes must NOT count,
        // because v2 has no performer entity and no per-scene add.
        var studios = new[] { Studio(1, null, "t-1"), Studio(2, null, "t-2"), Studio(3, null, null) };
        var performers = new[] { Studio(10, "p-1", "pt-1") };
        var videos = new[] { Scene(100, "v-1"), Scene(101, "v-2") };

        var result = Ext.SyncPreviewCore(
            studios, performers, videos, new WhisparrOptions { SelectedVersion = "v2" }, Adapter("v2"));

        Assert.Equal(2, result.Studios.WithId);
        Assert.Equal(1, result.Studios.Skipped);
        Assert.Equal(0, result.Performers.WithId);
        Assert.Equal(0, result.Performers.Skipped);
        Assert.Equal(0, result.Scenes.WithId);
        Assert.Equal(0, result.Scenes.Skipped);
    }

    [Fact]
    public void SyncPreviewCore_EmptyLibrary_IsAllZero()
    {
        var result = Ext.SyncPreviewCore(
            [], [], [], new WhisparrOptions { SelectedVersion = "v3" }, Adapter("v3"));

        Assert.Equal(0, result.Studios.WithId);
        Assert.Equal(0, result.Studios.Skipped);
        Assert.Equal(0, result.Performers.WithId);
        Assert.Equal(0, result.Scenes.WithId);
    }
}
