using System.Text.RegularExpressions;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// The closed loop: a plan fed back as its own input. Planning an item, committing that plan, and
/// planning again must reach <see cref="RenamerStatus.NoOp"/>, for every template the settings panel
/// ships and on both destination arms.
/// </summary>
/// <remarks>
/// Auto-rename-on-update rests on this and on nothing else — its only stop condition is a plan where
/// no item acts, so a plan that is not a fixed point re-raises <c>video.updated</c> forever, adding a
/// directory level or a copy of the template's decorations each pass until <c>FullPathMax</c> refuses
/// a path. A manual whole-library run has the same defect one pass at a time.
/// <para>
/// This is the loop's seam, and nothing else in the suite occupies it: the open-loop plan tests
/// (<see cref="RenamerPlannerTests"/>, <see cref="RoutingPlannerTests"/>) each plan exactly ONCE, and
/// pass one is correct even where the loop runs away — so no assertion about a single plan can see
/// this property, however many settings it is quantified over.
/// </para>
/// <para>
/// The commit between the two plans is modeled rather than executed, which is what keeps the property
/// observable at L0. The whole of a successful commit is one <see cref="RenamerFileMutation"/>, built
/// at the single site in <c>RenamerExecutor.ExecuteItemAsync</c>: a basename, a new parent folder from
/// the plan item's <c>TargetFolderPath</c>, and the item's <c>DerivedTitle</c> on the owning entity.
/// Applying those fields IS the executor's effect on the next plan's input; the disk move, the journal
/// row and the published event change nothing the planner reads.
/// </para>
/// <para>
/// The model is faithful UNDER TWO CONDITIONS, both of which hold in every cell this matrix reaches,
/// and neither of which the matrix could observe failing. The committed basename is the plan's
/// <c>NewBasename</c> only when no duplicate suffix was applied: the collision loop reassigns
/// <c>candidate</c> before the mutation is built, so on the suffixed path the executor commits a
/// basename this model never produces — and that is precisely the path where the next plan's input
/// differs by a decoration the loop could re-consume. A collision axis is deliberately NOT added; it
/// multiplies the matrix, and what is wanted here is that the limit is stated rather than covered. The
/// smaller asymmetry is the title: the write is conditional TWICE — the executor emits it only when
/// the plan-time <c>DerivedTitle</c> is non-empty, and <c>CoveRenamerDataPort.ApplyDerivedTitleAsync</c>
/// writes it only when the TRACKED row's title is still empty at save time, because a person can type
/// one between the preview and the run. The model reproduces the first condition only.
/// </para>
/// <para>
/// The tier above
/// proves what this one cannot — that the hook's guard is really wired to plan emptiness, in
/// <c>AutoRenamerHookTests</c> against a real host, and that the title write reaches the database, in
/// <c>RenamerExecutorIntegrationTests</c> — and this tier proves what those cannot afford to: the same
/// property across every shipped configuration.
/// </para>
/// </remarks>
[Trait("Tier", "L0")]
public sealed class PlanFixedPointTests
{
    // Forward-slash throughout, matching the RenamerFile DTO's own convention; OS-aware because
    // PathConfinement resolves both through Path.GetFullPath.
    private static string SourceRoot => OperatingSystem.IsWindows() ? "C:/library" : "/srv/library";

    /// <summary>A SECOND Cove library path, on another volume where the platform has volumes.</summary>
    private static string OtherRoot => OperatingSystem.IsWindows() ? "D:/routed" : "/mnt/routed";

    private const int RoutedStudioId = 3;

    /// <summary>The relative template rendered under whichever root the destination names.</summary>
    private const string SubfolderTemplate = "$performers";

    /// <summary>
    /// The three shapes a destination comes in, and the axis this matrix quantifies over. Every
    /// destination in the product is one of them — a rule's, the unorganized route's, and the default
    /// alike — so covering the three covers the model.
    /// </summary>
    public enum Shape
    {
        /// <summary>Neither a root nor a template: the shipped default, which moves nothing.</summary>
        MovesNothing,

        /// <summary>"The file's own library path", with a relative template rendered under it.</summary>
        OwnLibraryPath,

        /// <summary>A library path CHOSEN from the list, with the same template under it.</summary>
        ChosenLibraryPath,
    }

    private static Destination DestinationFor(Shape shape) => shape switch
    {
        Shape.MovesNothing => new Destination(),
        Shape.OwnLibraryPath => new Destination { Template = SubfolderTemplate },
        _ => new Destination { Root = OtherRoot, Template = SubfolderTemplate },
    };

    /// <summary>
    /// Enough passes for a runaway to be unmistakable in the failure message. A healthy configuration
    /// needs two: one to fix the name, one to confirm it.
    /// </summary>
    private const int MaxPasses = 25;

    private static RouteLookups StudioRouted(Shape shape) => new(
        StudioIdToDest: new Dictionary<int, Destination> { [RoutedStudioId] = DestinationFor(shape) },
        TagIdToDest: new Dictionary<int, Destination>(),
        PathExactToDest: new Dictionary<string, Destination>(StringComparer.Ordinal),
        PathRegexRules: []);

    // Carries every token the shipped presets can render ($date, $title, $studio, $performers, and
    // $resolution via Height) so no cell degrades to a shorter template than the panel offers.
    private static RenamerEntity Seed(bool titleSet) => new(
        EntityId: 1,
        Kind: RenamerFileKind.Video,
        Title: titleSet ? "My Film" : null,
        Code: "ABC-1",
        StudioName: "Acme Studio",
        Date: new DateOnly(2024, 3, 2),
        Organized: true,
        Performers: [new RenamerPerformer(1, "Ann Miller", false, "Female")],
        TagRefs: [(7, "hd")],
        Files: [new RenamerFile(
            FileId: 1,
            Kind: RenamerFileKind.Video,
            Basename: "raw.mp4",
            ParentFolderId: 5,
            ParentFolderPath: SourceRoot,
            Format: "mp4",
            Width: 1920,
            Height: 1080,
            Duration: 3600,
            VideoCodec: "h264",
            AudioCodec: "aac",
            FrameRate: 30,
            BitRate: 8_000_000)],
        StudioId: RoutedStudioId,
        ParentStudios: [(9, "Acme Group")],
        Director: "Jane Roe");

    /// <summary>
    /// The invariant, quantified over the shipped presets × destination shape × title present/absent ×
    /// matched-by-a-rule/unmatched.
    /// </summary>
    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task PlanThenCommitThenPlan_ReachesNoOp(
        string presetLabel, string filenameTemplate, Shape shape, bool titleSet, bool routed)
    {
        var destination = DestinationFor(shape);
        var options = new RenamerOptions
        {
            FilenameTemplate = filenameTemplate,

            // The DEFAULT destination. On the routed arm the matched rule replaces it, so the two arms
            // differ only in WHERE the same destination came from — which is what makes a rule silently
            // falling through to the default a change this matrix can see rather than one it repeats.
            FolderRoot = routed ? string.Empty : destination.Root,
            FolderTemplate = routed ? string.Empty : destination.Template,

            // Turned ON against its shipped default, because the title-less half of this matrix exists
            // to hold the fallback that used to re-consume its own output. Left at the default, every
            // title=empty cell would be gated out by RequiredFields and assert nothing about the loop —
            // an assertion made vacuous by a fixture value, which is the defect this file was written
            // for.
            FilenameAsTitle = true,
        };

        var replay = await ReplayAsync(
            options, Seed(titleSet), routed ? StudioRouted(shape) : RouteLookupsFixtures.RoutingNeutral);

        // A cell that stopped routing would take the DEFAULT destination and measure the other half of
        // the matrix while still reading as a routed pass — and with the two arms now differing only in
        // where the destination came from, nothing about the resulting path would look wrong. The
        // matched-rule label is the one fact that separates them, and the planner owns it.
        Assert.NotNull(replay.FirstActing);
        Assert.Equal(
            routed ? $"Studio:{RoutedStudioId}(direct)" : "Default", replay.FirstActing!.MatchedRule);

        Assert.True(
            replay.How == Settled.FixedPoint && replay.Renames <= 1,
            Describe(presetLabel, shape, titleSet, routed, replay));
    }

    /// <summary>
    /// The same closed loop for a file under NO Cove library path: it must settle, and it must settle
    /// where it already is.
    /// </summary>
    /// <remarks>
    /// Quantified over the same presets rather than shown on one, because "it stops" is the weaker half
    /// of the claim: a refusal that still rewrote the name would also stop, so each cell asserts the
    /// path is untouched too. This shape is ordinary rather than exotic — a file a destination rule once
    /// put outside the library is left in exactly it — and the outcome it pins is the one the rest of
    /// this file exists to rule out, since the only remaining anchor down here is the file's own parent,
    /// which the move itself would rewrite.
    /// <para>
    /// Only the OwnLibraryPath shape is quantified: it is the one that has to find a containing root at
    /// all. A destination that CHOSE its root carries its own anchor and never asks this question, and a
    /// chosen root that has since vanished is a different outcome with its own pin in
    /// <c>RoutingPlannerTests</c>.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(UnanchoredMatrix))]
    public async Task FolderTemplate_WithNoContainingLibraryPath_LeavesTheFileAlone(
        string presetLabel, string filenameTemplate, bool titleSet)
    {
        var options = new RenamerOptions
        {
            FilenameTemplate = filenameTemplate,
            FolderTemplate = SubfolderTemplate,
            FilenameAsTitle = true,
        };

        // A library that declares a path, just not one holding this file — the case a wholly unconfigured
        // library would not distinguish from a misconfigured read.
        var port = new FakeRenamerDataPort();
        port.SeedLibraryRoot(OperatingSystem.IsWindows() ? "C:/elsewhere" : "/srv/elsewhere");
        port.SeedEntity(Seed(titleSet));

        var item = Assert.Single(
            (await new RenamerPlanner(port).PlanAsync(
                RenamerFileKind.Video, 1, options, RouteLookupsFixtures.RoutingNeutral, default)).Items);

        string cell = $"{presetLabel} | title={(titleSet ? "set" : "empty")}";
        Assert.True(
            item.Status == RenamerStatus.SkipUnanchored,
            $"{cell}: expected SkipUnanchored, got {item.Status} -> '{item.NewFullPath}'.");
        Assert.Equal(item.OldFullPath, item.NewFullPath);
        Assert.Contains("library path", item.Reason);
    }

    public static TheoryData<string, string, bool> UnanchoredMatrix()
    {
        var cells = new TheoryData<string, string, bool>();

        foreach (var (label, template) in ShippedPresets())
        {
            foreach (bool titleSet in new[] { true, false })
            {
                cells.Add(label, template, titleSet);
            }
        }

        return cells;
    }

    /// <summary>
    /// The closed loop for a SOURCE-PATH rule: the one routing key the replay itself rewrites.
    /// </summary>
    /// <remarks>
    /// The matrix above is quantified only over keys that CANNOT stop matching — a studio id survives
    /// every pass, so a routed cell routes identically forever and the two arms differ in nothing the
    /// replay can change. A source-path rule keys on the parent folder, which the replay does rewrite,
    /// so pass 2 of a routed item is a different routing decision from pass 1. That composition is the
    /// one the matrix above cannot enter at all, and it is the only shape in which a rule and the
    /// default each relocate the same item.
    /// <para>
    /// Whether that double relocation is WANTED is a product question this file does not answer; what it
    /// pins is that the loop still terminates, which is what auto-rename-on-update rests on.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(SourceRoutedMatrix))]
    public async Task ASourcePathRule_ThenTheDefault_StillReachesNoOp(
        string presetLabel, string filenameTemplate)
    {
        var options = new RenamerOptions
        {
            FilenameTemplate = filenameTemplate,

            // The default is left at the moves-nothing shape deliberately, so the SECOND relocation
            // below is the default reclaiming an item whose rule has stopped matching — not a second
            // rule firing, which would prove nothing about the composition.
            FolderRoot = string.Empty,
            FolderTemplate = string.Empty,
            FilenameAsTitle = true,
        };

        var lookups = new RouteLookups(
            StudioIdToDest: new Dictionary<int, Destination>(),
            TagIdToDest: new Dictionary<int, Destination>(),
            PathExactToDest: new Dictionary<string, Destination>(StringComparer.Ordinal)
            {
                [SourceRoot] = Dests.At(OtherRoot, SubfolderTemplate),
            },
            PathRegexRules: []);

        var replay = await ReplayAsync(options, Seed(titleSet: true), lookups);

        string cell = $"{presetLabel} | source-path rule -> {OtherRoot}/{SubfolderTemplate}";

        // The rule really fired on pass 1. Without this the whole arm degrades silently into a second
        // copy of the unmatched case — every assertion below still passes, and it measures nothing.
        Assert.NotNull(replay.FirstActing);
        Assert.Equal("SourcePath:exact", replay.FirstActing!.MatchedRule);

        Assert.True(
            replay.How == Settled.FixedPoint,
            $"{cell}: ended {replay.How} after {replay.Renames} renames"
                + $"\n  last: {(replay.Renames > 0 ? replay.Trace[^1] : "(none)")}");

        // ONE, and the reason it is not two is the whole boundary of what this arm can observe. Pass 1
        // moves the item under the rule's root, which is what stops the rule matching. Pass 2 therefore
        // takes the DEFAULT — but the shipped default names neither a root nor a template, so isMove is
        // false and the item is measured against its own parent and left exactly where the rule put it.
        // The second relocation needs a NON-EMPTY default, which this cell deliberately does not carry,
        // so it is out of this arm's reach rather than absent from the product.
        Assert.Equal(1, replay.Renames);
    }

    /// <summary>
    /// The two-relocation composition itself: a rule relocates the item, and a NON-EMPTY default then
    /// relocates it a second time. It still terminates.
    /// </summary>
    /// <remarks>
    /// The arm above states why it cannot reach this — its default names neither a root nor a template,
    /// so pass 2 leaves the item where the rule put it. Supplying a configured default is the whole
    /// difference, and it is the ordinary reading of the two settings rather than a shape invented to
    /// satisfy an assertion: a path rule that sends one staging folder somewhere specific, plus a
    /// *Where files go* default for everything else.
    /// <para>
    /// <c>Renames == 2</c> is asserted exactly, and the arm above keeps its own <c>== 1</c>. Widening
    /// either to accommodate the other would let a cell that stopped composing pass as one that
    /// composed — the same reason the <c>MatchedRule</c> guard is here.
    /// </para>
    /// <para>
    /// WHERE THIS CONVERGES AND THE NEIGHBOUR THAT DOES NOT. Convergence rests on the default's rendered
    /// folder differing from the rule's key: pass 2 lands the item at <c>SourceRoot/$performers</c>,
    /// which is no longer exactly <c>SourceRoot</c>, so the source-path-exact rule cannot match a third
    /// time. Give that same default an EMPTY template and it lands the item back exactly on the rule's
    /// key, the rule fires again, and the two destinations trade the item forever — measured as still
    /// moving after every pass this file allows, with no skip to stop it. That configuration is
    /// self-contradictory (the rule says leave this folder, the default says return to it), but nothing
    /// refuses it and auto-rename-on-update would act on it indefinitely.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(SourceRoutedMatrix))]
    public async Task ASourcePathRule_ThenAConfiguredDefault_RelocatesTwice_AndStillReachesNoOp(
        string presetLabel, string filenameTemplate)
    {
        var options = new RenamerOptions
        {
            FilenameTemplate = filenameTemplate,

            // The one difference from the arm above: a default that actually moves something.
            FolderRoot = SourceRoot,
            FolderTemplate = SubfolderTemplate,
            FilenameAsTitle = true,
        };

        var lookups = new RouteLookups(
            StudioIdToDest: new Dictionary<int, Destination>(),
            TagIdToDest: new Dictionary<int, Destination>(),
            PathExactToDest: new Dictionary<string, Destination>(StringComparer.Ordinal)
            {
                [SourceRoot] = Dests.At(OtherRoot, SubfolderTemplate),
            },
            PathRegexRules: []);

        var replay = await ReplayAsync(options, Seed(titleSet: true), lookups);

        string cell = $"{presetLabel} | rule -> {OtherRoot}/{SubfolderTemplate}"
            + $" | default -> {SourceRoot}/{SubfolderTemplate}";

        Assert.NotNull(replay.FirstActing);
        Assert.Equal("SourcePath:exact", replay.FirstActing!.MatchedRule);

        Assert.True(
            replay.How == Settled.FixedPoint,
            $"{cell}: ended {replay.How} after {replay.Renames} renames"
                + $"\n  first: {(replay.Renames > 0 ? replay.Trace[0] : "(none)")}"
                + $"\n  last:  {(replay.Renames > 0 ? replay.Trace[^1] : "(none)")}");

        // Two, and exactly two: the rule's move, then the default reclaiming the item once the rule has
        // stopped matching. One would mean the default never fired and this cell is measuring the arm
        // above; three or more would mean the pair had not settled.
        Assert.Equal(2, replay.Renames);
    }

    public static TheoryData<string, string> SourceRoutedMatrix()
    {
        var cells = new TheoryData<string, string>();

        foreach (var (label, template) in ShippedPresets())
        {
            cells.Add(label, template);
        }

        return cells;
    }

    public static TheoryData<string, string, Shape, bool, bool> Matrix()
    {
        var cells = new TheoryData<string, string, Shape, bool, bool>();

        foreach (var (label, template) in ShippedPresets())
        {
            foreach (Shape shape in Enum.GetValues<Shape>())
            {
                foreach (bool titleSet in new[] { true, false })
                {
                    foreach (bool routed in new[] { false, true })
                    {
                        cells.Add(label, template, shape, titleSet, routed);
                    }
                }
            }
        }

        return cells;
    }

    // ── the shipped presets, read from the file that defines them ───────────────────────────────────

    // The panel's preset chips are TypeScript data and this invariant is C#. Reading the committed file
    // rather than transcribing it is what makes a preset added in the panel enter this matrix with no
    // C# edit — a hand-copied list would leave the new template unquantified and say nothing, which is
    // the exact shape of the defect this suite exists to catch. Copied next to the test assembly by
    // Renamer.Tests.csproj.
    private static readonly string PresetsPath = Path.Combine(AppContext.BaseDirectory, "presets.ts");

    /// <summary>Reads <c>PRESETS</c> out of the panel's <c>presets.ts</c>.</summary>
    /// <remarks>
    /// A parse that quietly matched a SUBSET would shrink the matrix while every remaining cell still
    /// passed, so the entry count is cross-checked against the field's own occurrences and any
    /// disagreement — including none at all — throws rather than returning what it found. The literal
    /// pattern accepts no backslash, so a template carrying an escape this parse cannot reproduce fails
    /// that cross-check instead of arriving subtly wrong.
    /// <para>
    /// The cross-check counts over the WHOLE file while the entries are parsed out of the array slice,
    /// and the two domains differ on purpose. Counting over the same slice the entries came from is a
    /// check that cannot fail in the direction that matters: a stray <c>];</c> inside a label or a
    /// comment ends the slice early, hiding the entries beyond it from BOTH counts, so they agree and
    /// the matrix silently shrinks. Counted over the whole file, a truncated slice makes them disagree.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<(string Label, string Template)> ShippedPresets()
    {
        string source = File.ReadAllText(PresetsPath);

        int start = source.IndexOf("export const PRESETS", StringComparison.Ordinal);
        int end = start < 0 ? -1 : source.IndexOf("];", start, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidOperationException(
                $"{PresetsPath} declares no 'export const PRESETS' array. The fixed-point matrix is "
                    + "built from the shipped preset chips and has nothing to quantify over without it.");
        }

        string body = source[start..end];

        var entries = Regex.Matches(
            body,
            """"
            label:\s*"([^"\\]*)"\s*,\s*filenameTemplate:\s*"([^"\\]*)"
            """");
        // The one occurrence outside the array: the field on the `interface Preset` declaration above it.
        const int InterfaceDeclarations = 1;
        int declared = Regex.Count(source, @"\bfilenameTemplate\b") - InterfaceDeclarations;

        if (entries.Count == 0 || entries.Count != declared)
        {
            throw new InvalidOperationException(
                $"PRESETS declares {declared} filenameTemplate entries but this parse matched "
                    + $"{entries.Count}. The matrix would cover a subset of the shipped presets and "
                    + $"still report green — fix the parse in {nameof(PlanFixedPointTests)} or the "
                    + "shape of presets.ts.");
        }

        return [.. entries.Select(m => (m.Groups[1].Value, m.Groups[2].Value))];
    }

    // ── the replay ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>How a replay ended.</summary>
    /// <remarks>
    /// <see cref="FixedPoint"/> is the only convergence. <see cref="Blocked"/> is a runaway that a
    /// skip finally stopped — usually the <c>FullPathMax</c> re-check — and reading a skip as
    /// convergence is what made an earlier throwaway sweep report every defective configuration green.
    /// </remarks>
    private enum Settled { FixedPoint, Blocked, StillMoving }

    private sealed record Replay(
        Settled How,
        IReadOnlyList<string> Trace,
        RenamerPlanItem? FirstActing,
        RenamerPlanItem? Terminal)
    {
        public int Renames => Trace.Count;
    }

    private static async Task<Replay> ReplayAsync(
        RenamerOptions options, RenamerEntity seed, RouteLookups lookups)
    {
        var port = new FakeRenamerDataPort();
        port.SeedLibraryRoot(SourceRoot);
        port.SeedLibraryRoot(OtherRoot);   // the root a ChosenLibraryPath destination picks
        var planner = new RenamerPlanner(port);

        var entity = seed;
        var trace = new List<string>();
        RenamerPlanItem? firstActing = null;

        for (int pass = 1; pass <= MaxPasses; pass++)
        {
            port.SeedEntity(entity);
            var item = Assert.Single(
                (await planner.PlanAsync(entity.Kind, entity.EntityId, options, lookups, default)).Items);

            if (item.Status == RenamerStatus.NoOp)
            {
                return new Replay(Settled.FixedPoint, trace, firstActing, item);
            }

            if (item.Status is not (RenamerStatus.Rename or RenamerStatus.Move))
            {
                return new Replay(Settled.Blocked, trace, firstActing, item);
            }

            firstActing ??= item;
            trace.Add(item.NewFullPath);

            // The executor's whole effect on the next plan's input: see the class remarks. The title
            // arm is not decoration — a replay that applied only the file half would keep re-deriving
            // the title from a basename this loop has already rewritten, which is exactly the state the
            // executor's save leaves behind it and therefore what the next plan must read.
            entity = entity with
            {
                Title = item.DerivedTitle ?? entity.Title,
                Files = [entity.Files[0] with
                {
                    Basename = item.NewBasename,
                    ParentFolderPath = item.TargetFolderPath,
                }],
            };
        }

        return new Replay(Settled.StillMoving, trace, firstActing, null);
    }

    private static string Describe(
        string presetLabel, Shape shape, bool titleSet, bool routed, Replay replay)
    {
        var destination = DestinationFor(shape);
        string destinationLabel = shape switch
        {
            Shape.MovesNothing => "(moves nothing)",
            Shape.OwnLibraryPath => $"own library path + {destination.Template}",
            _ => $"{destination.Root} + {destination.Template}",
        };
        string cell = $"{presetLabel} | destination={destinationLabel}"
            + $" | title={(titleSet ? "set" : "empty")} | {(routed ? "routed" : "unmatched")}";

        string ending = replay.How switch
        {
            Settled.FixedPoint =>
                $"churned through {replay.Renames} renames before settling",
            Settled.Blocked =>
                $"kept producing a new destination for {replay.Renames} renames until "
                    + $"{replay.Terminal!.Status} stopped it ({replay.Terminal.Reason})",
            _ => $"was still producing a new destination after {MaxPasses} passes",
        };

        return $"{cell}: {ending}."
            + $"\n  first: {(replay.Renames > 0 ? replay.Trace[0] : "(none)")}"
            + $"\n  last:  {(replay.Renames > 0 ? replay.Trace[^1] : "(none)")}";
    }
}
