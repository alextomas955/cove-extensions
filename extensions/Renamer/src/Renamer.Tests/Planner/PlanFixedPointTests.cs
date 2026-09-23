using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

public sealed class PlanFixedPointTests
{
    private const string FolderPath = "media/videos";

    // A template rendering more than a bare $title - the shape whose own decorations the derivation
    // re-consumed.
    private const string DecoratedTemplate = "{$date - }$title{ [$resolution]}";

    // Enough passes for a runaway to be unmistakable in the failure message; a healthy
    // configuration needs one rename and one confirming pass.
    private const int MaxPasses = 12;

    private static RenamerFile FileRow(int id, string basename) => new(
        FileId: id, Kind: RenamerFileKind.Video, Basename: basename, ParentFolderId: 5,
        ParentFolderPath: FolderPath, Width: 3840, Height: 2160);

    private static RenamerEntity Entity(string? title, params RenamerFile[] files) => new(
        EntityId: 10, Kind: RenamerFileKind.Video, Title: title, Code: null, StudioName: null,
        Date: new DateOnly(2021, 3, 14), Organized: true, Performers: [], TagRefs: [], Files: files);

    [Fact]
    public async Task ATitlelessItem_UnderADecoratedTemplate_SettlesAfterOneRename()
    {
        var replay = await ReplayAsync(
            new RenamerOptions { FilenameTemplate = DecoratedTemplate, FilenameAsTitle = true },
            Entity(null, FileRow(1, "raw clip.mkv")));

        Assert.True(replay.How == Settled.FixedPoint, replay.ToString());
        Assert.Equal([$"{FolderPath}/2021-03-14 - raw clip [4K].mkv"], replay.Trace);
    }

    [Fact]
    public async Task ATitlelessMultiFileItem_DerivesOneTitleForTheWholeItem_AndSettles()
    {
        var replay = await ReplayAsync(
            new RenamerOptions { FilenameTemplate = DecoratedTemplate, FilenameAsTitle = true },
            Entity(null, FileRow(1, "raw clip.mkv"), FileRow(2, "extra angle.mp4")));

        Assert.True(replay.How == Settled.FixedPoint, replay.ToString());
        Assert.Equal(
            [
                $"{FolderPath}/2021-03-14 - raw clip [4K].mkv",
                $"{FolderPath}/2021-03-14 - raw clip [4K].mp4",
            ],
            replay.Trace);
        Assert.Equal(["raw clip", "raw clip"], replay.FirstDerivedTitles);
    }

    [Fact]
    public async Task WithTheFallbackOff_NoTitleIsDerived()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(null, FileRow(1, "raw clip.mkv")));
        var options = new RenamerOptions
        {
            FilenameTemplate = DecoratedTemplate,
            FilenameAsTitle = false,
            RequiredFields = [],
        };

        var item = Assert.Single(
            (await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, 10, options, default)).Items);

        Assert.Equal(RenamerStatus.Rename, item.Status);
        Assert.Null(item.DerivedTitle);
    }

    [Fact]
    public async Task AnItemThatAlreadyHasATitle_DerivesNothing_AndStillSettles()
    {
        var replay = await ReplayAsync(
            new RenamerOptions { FilenameTemplate = DecoratedTemplate, FilenameAsTitle = true },
            Entity("My Film", FileRow(1, "raw clip.mkv")));

        Assert.True(replay.How == Settled.FixedPoint, replay.ToString());
        Assert.Equal([$"{FolderPath}/2021-03-14 - My Film [4K].mkv"], replay.Trace);
        Assert.Equal([null], replay.FirstDerivedTitles);
    }

    // How a replay ended. FixedPoint is the only convergence. Blocked is a runaway a skip finally
    // stopped - usually the FullPathMax re-check - and reading one as convergence is what lets a
    // defective configuration report green.
    private enum Settled { FixedPoint, Blocked, StillMoving }

    private sealed record Replay(
        Settled How,
        IReadOnlyList<string> Trace,
        IReadOnlyList<string?> FirstDerivedTitles,
        RenamerPlanItem? Terminal)
    {
        public override string ToString() => How switch
        {
            Settled.FixedPoint => $"settled after {Trace.Count} renames: {string.Join(" | ", Trace)}",
            Settled.Blocked =>
                $"kept producing a new name for {Trace.Count} renames until {Terminal!.Status} stopped it"
                    + $" ({Terminal.Reason}): {string.Join(" | ", Trace)}",
            _ => $"still producing a new name after {MaxPasses} passes: {string.Join(" | ", Trace)}",
        };
    }

    // Plans seed, applies the commit the plan describes, and plans again until the item settles or
    // MaxPasses is spent.
    private static async Task<Replay> ReplayAsync(RenamerOptions options, RenamerEntity seed)
    {
        var port = new FakeRenamerDataPort();
        var planner = new RenamerPlanner(port);

        var entity = seed;
        var trace = new List<string>();
        IReadOnlyList<string?> firstDerived = [];

        for (int pass = 1; pass <= MaxPasses; pass++)
        {
            port.SeedEntity(entity);
            var items = (await planner.PlanAsync(entity.Kind, entity.EntityId, options, default)).Items;

            if (items.All(i => i.Status == RenamerStatus.NoOp))
            {
                return new Replay(Settled.FixedPoint, trace, firstDerived, null);
            }

            var blocked = items.FirstOrDefault(
                i => i.Status is not (RenamerStatus.Rename or RenamerStatus.Move or RenamerStatus.NoOp));
            if (blocked is not null)
            {
                return new Replay(Settled.Blocked, trace, firstDerived, blocked);
            }

            if (firstDerived.Count == 0)
            {
                firstDerived = [.. items.Select(i => i.DerivedTitle)];
            }

            trace.AddRange(items.Where(i => i.Status != RenamerStatus.NoOp).Select(i => i.NewFullPath));

            // The executor's whole effect on the next plan's input. The title arm is the one under
            // test: applied only to the files, the next pass re-derives the title from a basename this
            // loop has already rewritten. The first non-empty derivation wins, mirroring the port's
            // still-empty re-check across the files of one item, which save one at a time.
            entity = entity with
            {
                Title = items.Select(i => i.DerivedTitle).FirstOrDefault(t => !string.IsNullOrEmpty(t))
                    ?? entity.Title,
                Files = [.. entity.Files.Select(f =>
                {
                    var planned = items.First(i => i.FileId == f.FileId);
                    return f with
                    {
                        Basename = planned.NewBasename,
                        ParentFolderPath = planned.TargetFolderPath,
                    };
                })],
            };
        }

        return new Replay(Settled.StillMoving, trace, firstDerived, null);
    }
}
