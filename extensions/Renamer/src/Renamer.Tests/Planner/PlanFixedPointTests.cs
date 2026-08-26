using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// The closed loop: a plan committed and then planned again must reach
/// <see cref="RenamerStatus.NoOp"/>.
/// </summary>
/// <remarks>
/// The filename-as-title fallback is what puts a loop here at all: the title is derived from the
/// basename the previous pass wrote, and the basename is rendered from the title, so a template
/// carrying anything besides a bare <c>$title</c> wraps its own decorations again every pass. The
/// single-pass planner tests cannot see it - pass one is correct even where the loop runs away - so
/// this is the seam that holds the property. See <c>MetadataProjector.DerivedTitle</c>.
/// <para>
/// The commit between two plans is MODELLED, not executed, which is what keeps this tier pure: a
/// successful commit is one <c>RenamerFileMutation</c>, and its whole effect on the next plan's input
/// is the basename, the parent folder, and the derived title on the owning entity. The disk move, the
/// journal row and the published event change nothing the planner reads. That the executor really
/// emits the title write, and that it reaches the database, is pinned against a real context in
/// <c>RenamerExecutorIntegrationTests</c>.
/// </para>
/// <para>
/// Every expected name below is transcribed by hand from the arrangement. Asking the engine what it
/// would render would produce an expectation that agrees with the code under test however far the two
/// drift.
/// </para>
/// </remarks>
public sealed class PlanFixedPointTests
{
    private const string FolderPath = "media/videos";

    /// <summary>A template rendering more than a bare <c>$title</c> - the shape whose own decorations the derivation re-consumed.</summary>
    private const string DecoratedTemplate = "{$date - }$title{ [$resolution]}";

    /// <summary>Enough passes for a runaway to be unmistakable in the failure message; a healthy configuration needs one rename and one confirming pass.</summary>
    private const int MaxPasses = 12;

    private static RenamerFile FileRow(int id, string basename) => new(
        FileId: id, Kind: RenamerFileKind.Video, Basename: basename, ParentFolderId: 5,
        ParentFolderPath: FolderPath, Height: 2160);

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
        Assert.Equal([$"{FolderPath}/2021-03-14 - raw clip [4k].mkv"], replay.Trace);
    }

    /// <summary>
    /// A title belongs to the ITEM, so a multi-file item derives ONE title however many files it has.
    /// </summary>
    /// <remarks>
    /// Derived from the file being projected instead, an item gets as many candidate titles as it has
    /// files and the one that survives is decided by whichever file the executor saved last. The two
    /// files carry different extensions, so the pair never collides and the loop is the only thing
    /// this measures.
    /// </remarks>
    [Fact]
    public async Task ATitlelessMultiFileItem_DerivesOneTitleForTheWholeItem_AndSettles()
    {
        var replay = await ReplayAsync(
            new RenamerOptions { FilenameTemplate = DecoratedTemplate, FilenameAsTitle = true },
            Entity(null, FileRow(1, "raw clip.mkv"), FileRow(2, "extra angle.mp4")));

        Assert.True(replay.How == Settled.FixedPoint, replay.ToString());
        Assert.Equal(
            [
                $"{FolderPath}/2021-03-14 - raw clip [4k].mkv",
                $"{FolderPath}/2021-03-14 - raw clip [4k].mp4",
            ],
            replay.Trace);
        Assert.Equal(["raw clip", "raw clip"], replay.FirstDerivedTitles);
    }

    /// <summary>With the fallback off there is nothing to record, and the plan says so.</summary>
    /// <remarks>
    /// The required-fields gate is cleared so the item genuinely acts: left at its shipped
    /// <c>title</c> the item would be gated out, and an item that never acts carries no derived title
    /// for a reason unrelated to the setting.
    /// </remarks>
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

        Assert.Equal(RenamerStatus.Renamer, item.Status);
        Assert.Null(item.DerivedTitle);
    }

    /// <summary>A stored title is never re-derived, so a rename never rewrites metadata a person owns.</summary>
    [Fact]
    public async Task AnItemThatAlreadyHasATitle_DerivesNothing_AndStillSettles()
    {
        var replay = await ReplayAsync(
            new RenamerOptions { FilenameTemplate = DecoratedTemplate, FilenameAsTitle = true },
            Entity("My Film", FileRow(1, "raw clip.mkv")));

        Assert.True(replay.How == Settled.FixedPoint, replay.ToString());
        Assert.Equal([$"{FolderPath}/2021-03-14 - My Film [4k].mkv"], replay.Trace);
        Assert.Equal([null], replay.FirstDerivedTitles);
    }

    /// <summary>How a replay ended.</summary>
    /// <remarks>
    /// <see cref="FixedPoint"/> is the only convergence. <see cref="Blocked"/> is a runaway a skip
    /// finally stopped - usually the <c>FullPathMax</c> re-check - and reading one as convergence is
    /// what lets a defective configuration report green.
    /// </remarks>
    private enum Settled { FixedPoint, Blocked, StillMoving }

    /// <param name="How">How the loop ended.</param>
    /// <param name="Trace">Every acting item's new full path, in pass then file order.</param>
    /// <param name="FirstDerivedTitles">The first acting pass's per-item derived titles, in file order.</param>
    /// <param name="Terminal">The item that ended a <see cref="Settled.Blocked"/> run.</param>
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

    /// <summary>
    /// Plans <paramref name="seed"/>, applies the commit the plan describes, and plans again until the
    /// item settles or <see cref="MaxPasses"/> is spent.
    /// </summary>
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
                i => i.Status is not (RenamerStatus.Renamer or RenamerStatus.Move or RenamerStatus.NoOp));
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
