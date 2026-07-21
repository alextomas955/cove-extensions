using System.Runtime.CompilerServices;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Monitor;
using WhisparrSync.Options;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Safety;

/// <summary>
/// The library-sync loop-safety contract (SYNC-ALL-04): the "Sync my library to Whisparr" fan-out can NEVER
/// grab. Proven three ways — the op discriminant has no grab member, no planned unit ever carries one, and
/// (the strongest, a structural source guard mirroring <see cref="NoMutationTests"/>) the fan-out DISPATCH
/// source dispatches only the non-grabbing runner verbs, so it cannot reach a search/grab path.
/// </summary>
public sealed class SyncLibraryLoopSafetyTests
{
    private static IWhisparrAdapter Adapter(string version)
        => AdapterSelector.SelectForVersion(version, new WhisparrClient(new HttpClient()))!;

    private static CoveEntityRef Entity(int id, string stashId) => new(id, [stashId], []);

    private static CoveVideo Scene(int id, string stashId) => new(id, $"Scene {id}", null, [stashId], [], [], []);

    [Fact]
    public void SyncOp_HasNoGrabMember()
    {
        // The loop-safe verbs and nothing else: a future grab op cannot slip in un-noticed. RegisterEntity is a
        // non-grabbing site-add, so it is a member; a "Search" member never is.
        Assert.Equal(
            new[]
            {
                nameof(Ext.SyncOp.ReflectOwned), nameof(Ext.SyncOp.Monitor),
                nameof(Ext.SyncOp.Add), nameof(Ext.SyncOp.RegisterEntity),
            },
            Enum.GetNames<Ext.SyncOp>());
        Assert.DoesNotContain(Enum.GetNames<Ext.SyncOp>(), n => n.Contains("Search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildSyncUnits_NeverPlansAGrabOp()
    {
        var studios = new[] { Entity(1, "s-1") };
        var performers = new[] { Entity(10, "p-1") };
        var videos = new[] { Scene(100, "v-1") };

        var units = Ext.BuildSyncUnits(
            studios, performers, videos, alsoMonitor: true, MonitorScope.AllScenes,
            new WhisparrOptions { SelectedVersion = "v3" }, Adapter("v3"));

        Assert.NotEmpty(units);
        Assert.All(units, u => Assert.True(
            u.Op is Ext.SyncOp.ReflectOwned or Ext.SyncOp.Monitor or Ext.SyncOp.Add or Ext.SyncOp.RegisterEntity));
    }

    [Fact]
    public void SyncLibrarySource_DispatchesOnlyNonGrabbingRunnerVerbs()
    {
        var code = string.Join(
            '\n',
            File.ReadAllLines(SyncLibrarySourcePath())
                .Select(StripComment)
                .Where(line => !string.IsNullOrWhiteSpace(line)));

        // Any of these in the fan-out dispatch would open a grab path — the whole pass must register/monitor only.
        string[] forbidden =
        [
            "BatchOp.Search", "BatchOp.SearchUpgrades", "EntityBatchOp.Search",
            "SearchSceneAsync", "SearchForUpgradesAsync", "SearchAllMonitoredAsync", "MoviesSearch",
        ];
        foreach (var verb in forbidden)
        {
            Assert.DoesNotContain(verb, code);
        }
    }

    // Strip line + inline comments so a doc comment mentioning "search" can neither satisfy nor invalidate the
    // source guard — only real code lines are inspected (mirrors NoMutationTests).
    private static string StripComment(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }

    // The source sits beside this test project: ../../WhisparrSync/WhisparrSync.SyncLibrary.cs (this file lives
    // one concern-subfolder deep under the test project root).
    private static string SyncLibrarySourcePath([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(thisFile)!, "..", "..", "WhisparrSync", "WhisparrSync.SyncLibrary.cs"));
}
