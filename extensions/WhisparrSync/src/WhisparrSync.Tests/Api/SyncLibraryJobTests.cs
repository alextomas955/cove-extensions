using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Monitor;
using WhisparrSync.Options;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api;

// Bare-CI pure-unit tier: references only WhisparrSync DTOs + the pure adapter capability flags, never a Cove
// type. Covers Ext.BuildSyncUnits — the "Sync my library to Whisparr" fan-out planner. Proves the v3/v2 unit
// shape, decoupled add-vs-monitor with the user scope, and that id-less entities are skipped.
public sealed class SyncLibraryJobTests
{
    private static IWhisparrAdapter Adapter(string version)
        => AdapterSelector.SelectForVersion(version, new WhisparrClient(new HttpClient()))!;

    private static WhisparrOptions Options(string version) => new() { SelectedVersion = version };

    private static CoveEntityRef Entity(int id, string? stashId, string? tpdbId)
        => new(id, stashId is null ? [] : [stashId], tpdbId is null ? [] : [tpdbId]);

    private static CoveVideo Scene(int id, string? stashId)
        => new(id, $"Scene {id}", null, stashId is null ? [] : [stashId], [], [], []);

    private static IReadOnlyList<int> CoveIdsOf(IEnumerable<Ext.SyncUnit> units, Ext.SyncOp op, EntityKind kind)
        => [.. units.Where(u => u.Op == op && u.Video is null && u.Kind == kind).Select(u => u.CoveId)];

    private static IReadOnlyList<int> AddCoveIdsOf(IEnumerable<Ext.SyncUnit> units)
        => [.. units.Where(u => u.Op == Ext.SyncOp.Add).Select(u => u.CoveId)];

    [Fact]
    public void BuildSyncUnits_V3_WithMonitor_PlansReflectAddAndScopedMonitor_SkippingIdLess()
    {
        var studios = new[] { Entity(1, "s-1", null), Entity(2, "s-2", null), Entity(3, null, null) };
        var performers = new[] { Entity(10, "p-1", null), Entity(11, null, null) };
        var videos = new[] { Scene(100, "v-1"), Scene(101, "v-2"), Scene(102, null) };

        var units = Ext.BuildSyncUnits(
            studios, performers, videos, alsoMonitor: true, MonitorScope.AllScenes, Options("v3"), Adapter("v3"));

        // Reflect-owned: every studio + performer that carries a StashDB id (the id-less 3 / 11 are absent).
        Assert.Equal(new[] { 1, 2 }, CoveIdsOf(units, Ext.SyncOp.ReflectOwned, EntityKind.Studio));
        Assert.Equal(new[] { 10 }, CoveIdsOf(units, Ext.SyncOp.ReflectOwned, EntityKind.Performer));

        // Add: every v3 scene that carries a StashDB id (the id-less 102 is absent).
        Assert.Equal(new[] { 100, 101 }, AddCoveIdsOf(units));

        // Monitor: studios + performer with an id, each carrying the user-chosen scope.
        Assert.Equal(new[] { 1, 2 }, CoveIdsOf(units, Ext.SyncOp.Monitor, EntityKind.Studio));
        Assert.Equal(new[] { 10 }, CoveIdsOf(units, Ext.SyncOp.Monitor, EntityKind.Performer));
        Assert.All(units.Where(u => u.Op == Ext.SyncOp.Monitor), u => Assert.Equal(MonitorScope.AllScenes, u.Scope));
    }

    [Fact]
    public void BuildSyncUnits_V3_WithoutMonitor_PlansTheSameAddAndReflect_ButNoMonitor()
    {
        var studios = new[] { Entity(1, "s-1", null) };
        var performers = new[] { Entity(10, "p-1", null) };
        var videos = new[] { Scene(100, "v-1") };

        var units = Ext.BuildSyncUnits(
            studios, performers, videos, alsoMonitor: false, MonitorScope.NewReleases, Options("v3"), Adapter("v3"));

        // Add and monitor are decoupled (SYNC-ALL-02): the add/reflect legs run regardless of the monitor choice.
        Assert.Equal(new[] { 1 }, CoveIdsOf(units, Ext.SyncOp.ReflectOwned, EntityKind.Studio));
        Assert.Equal(new[] { 10 }, CoveIdsOf(units, Ext.SyncOp.ReflectOwned, EntityKind.Performer));
        Assert.Equal(new[] { 100 }, AddCoveIdsOf(units));
        Assert.DoesNotContain(units, u => u.Op == Ext.SyncOp.Monitor);
    }

    [Fact]
    public void BuildSyncUnits_V2_IsStudiosOnly_NoPerformerNoSceneAdd()
    {
        // v2 studios resolve by ThePornDB id; the StashDB ids on the performer/scenes must NOT plan a unit,
        // because v2 has no performer entity and no per-scene add.
        var studios = new[] { Entity(1, null, "t-1"), Entity(2, null, "t-2"), Entity(3, null, null) };
        var performers = new[] { Entity(10, "p-1", "pt-1") };
        var videos = new[] { Scene(100, "v-1"), Scene(101, "v-2") };

        var units = Ext.BuildSyncUnits(
            studios, performers, videos, alsoMonitor: true, MonitorScope.NewReleases, Options("v2"), Adapter("v2"));

        Assert.Equal(new[] { 1, 2 }, CoveIdsOf(units, Ext.SyncOp.ReflectOwned, EntityKind.Studio));
        Assert.Equal(new[] { 1, 2 }, CoveIdsOf(units, Ext.SyncOp.Monitor, EntityKind.Studio));
        // No performer units (reflect or monitor), and no scene-add units — none are a v2 concept.
        Assert.DoesNotContain(units, u => u.Kind == EntityKind.Performer && u.Video is null);
        Assert.DoesNotContain(units, u => u.Op == Ext.SyncOp.Add);
    }

    [Fact]
    public void BuildSyncUnits_EmptyLibrary_PlansNothing()
    {
        var units = Ext.BuildSyncUnits(
            [], [], [], alsoMonitor: true, MonitorScope.NewReleases, Options("v3"), Adapter("v3"));

        Assert.Empty(units);
    }
}
