using WhisparrSync.Addressing;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;

namespace WhisparrSync.Tests.Options;

// The outbound pair mirrors the import refusals: one entry per root rather than per folder, so a
// library of any size leaves these the same length.
public sealed class FolderMappingOptionsTests
{
    private const string CoveRoot = "G:/Downloads/P";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public void ARunThatCouldNotAddressARootStoresTheReasonAndThePathsTried()
    {
        var folded = OutboundRefusalProjector.Fold(
            [],
            [Refused(CoveRoot, FolderAgreementRefusal.NothingResolved, "/data/Blue Harbor/a.mp4")],
            addressed: []);

        var entry = Assert.Single(folded);
        Assert.Equal(CoveRoot, entry.Root);
        Assert.Equal(FolderAgreementRefusal.NothingResolved, entry.Refusal);
        Assert.Equal(["/data/Blue Harbor/a.mp4"], entry.PathsTried);
    }

    // The reason and the paths are what the last run established. An entry that accumulated would
    // grow with the runs an operator makes and would report a reason that no longer holds.
    [Fact]
    public void ASecondRunOverOneRootReplacesItsEntry()
    {
        var first = OutboundRefusalProjector.Fold(
            [],
            [Refused(CoveRoot, FolderAgreementRefusal.NothingResolved, "/data/a.mp4")],
            addressed: []);

        var second = OutboundRefusalProjector.Fold(
            first,
            [Refused(CoveRoot, FolderAgreementRefusal.InstanceDeclaresNoRoot)],
            addressed: []);

        var entry = Assert.Single(second);
        Assert.Equal(FolderAgreementRefusal.InstanceDeclaresNoRoot, entry.Refusal);
        Assert.Empty(entry.PathsTried);
    }

    [Fact]
    public void ARootARunAddressedLosesItsEntryAndTheOthersKeepTheirs()
    {
        var stored = OutboundRefusalProjector.Fold(
            [],
            [
                Refused(CoveRoot, FolderAgreementRefusal.NothingResolved, "/data/a.mp4"),
                Refused("/shared", FolderAgreementRefusal.InstanceCannotBeAsked),
            ],
            addressed: []);

        var folded = OutboundRefusalProjector.Fold(stored, refused: [], addressed: [CoveRoot]);

        Assert.Equal("/shared", Assert.Single(folded).Root);
    }

    [Fact]
    public void TwoSpellingsOfOneRootDifferingOnlyByATrailingSeparatorAreOneEntry()
    {
        var stored = OutboundRefusalProjector.Fold(
            [],
            [Refused(CoveRoot + "/", FolderAgreementRefusal.NothingResolved)],
            addressed: []);

        Assert.Equal(CoveRoot, Assert.Single(stored).Root);
        Assert.Empty(OutboundRefusalProjector.Fold(stored, refused: [], addressed: [CoveRoot]));
    }

    [Fact]
    public void ThePathsOneEntryKeepsAreBoundedByTheNamedConstant()
    {
        var entry = new OutboundRootRefusal
        {
            Root = CoveRoot,
            PathsTried = [.. Enumerable.Range(0, 20).Select(index => $"/data/{index}.mp4")],
        };

        Assert.Equal(OutboundRootRefusal.PathsTriedKept, entry.PathsTried.Count);
        Assert.Equal("/data/0.mp4", entry.PathsTried[0]);
    }

    // Both roots are normalised, so a path typed with a trailing separator keys and rebuilds the
    // same way one typed without it does.
    [Fact]
    public void AMappingsTwoRootsAreBothNormalised()
    {
        var mapping = new OutboundRootMapping { CoveRoot = CoveRoot + "/", InstanceRoot = "/data/" };

        Assert.Equal(CoveRoot, mapping.CoveRoot);
        Assert.Equal("/data", mapping.InstanceRoot);
    }

    [Fact]
    public void AMappingIsStoredOncePerRootAndABlankPathRemovesIt()
    {
        var stored = OutboundRefusalProjector.WithMapping([], CoveRoot, "/data");
        Assert.Equal("/data", Assert.Single(stored).InstanceRoot);

        var moved = OutboundRefusalProjector.WithMapping(stored, CoveRoot + "/", "/mnt/media");
        Assert.Equal("/mnt/media", Assert.Single(moved).InstanceRoot);

        Assert.Empty(OutboundRefusalProjector.WithMapping(moved, CoveRoot, "  "));
    }

    /// <summary>
    /// A save followed by a load returns an equal record, including both collection members. Record
    /// equality compares a list by reference, so a round-trip's fresh list is the case a default
    /// implementation reports as changed.
    /// </summary>
    [Fact]
    public async Task ARoundTripThroughTheStoreReturnsAnEqualRecord()
    {
        var store = new FakeStore();
        var saved = Populated();

        await new OptionsStore(store).SaveAsync(saved, TestCt);
        var loaded = await new OptionsStore(store).LoadAsync(TestCt);

        Assert.Equal(saved, loaded);
        Assert.Equal(saved.GetHashCode(), loaded.GetHashCode());
        Assert.Equal(2, loaded.OutboundRefusals.Count);
        Assert.Equal(2, loaded.OutboundRefusals[0].PathsTried.Count);
        Assert.Equal("/data", Assert.Single(loaded.OutboundMappings).InstanceRoot);
    }

    /// <summary>The refusal reason survives the blob as its own spelling rather than an ordinal.</summary>
    [Fact]
    public async Task TheRefusalReasonRoundTripsAsAString()
    {
        var store = new FakeStore();

        await new OptionsStore(store).SaveAsync(Populated(), TestCt);

        var blob = await store.GetAsync(OptionsStore.Key, TestCt);
        Assert.Contains("\"nothingResolved\"", blob, StringComparison.Ordinal);
    }

    /// <summary>
    /// Records differing only in how many entries a collection holds are not equal, at either level.
    /// </summary>
    [Fact]
    public void RecordsDifferingOnlyInHowManyEntriesTheyHoldAreNotEqual()
    {
        var saved = Populated();

        Assert.NotEqual(saved, saved with { OutboundRefusals = [saved.OutboundRefusals[0]] });
        Assert.NotEqual(saved, saved with { OutboundMappings = [] });
        Assert.NotEqual(
            saved.OutboundRefusals[0],
            saved.OutboundRefusals[0] with { PathsTried = [saved.OutboundRefusals[0].PathsTried[0]] });
    }

    /// <summary>A stored blob naming either collection as null binds it as empty.</summary>
    /// <remarks>
    /// The accessor is what does this. An initialiser runs only for an ABSENT key, and a blob naming
    /// the member as null binds it as null.
    /// </remarks>
    [Fact]
    public async Task ABlobNamingEitherCollectionAsNullBindsItAsEmpty()
    {
        var store = new FakeStore();
        await store.SetAsync(
            OptionsStore.Key, """{"outboundRefusals":null,"outboundMappings":null}""", TestCt);

        var loaded = await new OptionsStore(store).LoadAsync(TestCt);

        Assert.Empty(loaded.OutboundRefusals);
        Assert.Empty(loaded.OutboundMappings);
    }

    /// <summary>
    /// A blob naming one entry's paths as null binds them as empty, which the outer restore does not
    /// reach.
    /// </summary>
    [Fact]
    public async Task ABlobNamingOneEntrysPathsAsNullBindsThemAsEmpty()
    {
        var store = new FakeStore();
        await store.SetAsync(
            OptionsStore.Key,
            """{"outboundRefusals":[{"root":"/shared","refusal":"nothingResolved","pathsTried":null}]}""",
            TestCt);

        var loaded = await new OptionsStore(store).LoadAsync(TestCt);

        Assert.Empty(Assert.Single(loaded.OutboundRefusals).PathsTried);
    }

    /// <summary>
    /// The settings page reads one line per root the instance could not see, and nothing for a root
    /// it could.
    /// </summary>
    [Fact]
    public void TheViewCarriesOneLinePerRootTheInstanceCouldNotSee()
    {
        var view = FolderAgreementView.From(
            [
                new OutboundRootRefusal
                {
                    Root = CoveRoot,
                    Refusal = FolderAgreementRefusal.NothingResolved,
                    PathsTried = ["/data/Blue Harbor/a.mp4"],
                },
            ],
            []);

        var line = Assert.Single(view.Roots);
        Assert.Equal(CoveRoot, line.Root);
        Assert.Equal(FolderAgreementRefusal.NothingResolved, line.Refusal);
        Assert.Equal(["/data/Blue Harbor/a.mp4"], line.PathsTried);
        Assert.Null(line.Mapping);
    }

    /// <summary>A root whose stored path is working reads as a line carrying that path alone.</summary>
    /// <remarks>
    /// The field that withdraws a path is offered beside the line. A root that vanished the moment
    /// its path worked could not be reviewed or withdrawn through the product at all.
    /// </remarks>
    [Fact]
    public void ARootWhoseStoredPathIsWorkingReadsAsALineCarryingThatPath()
    {
        var view = FolderAgreementView.From(
            [], [new OutboundRootMapping { CoveRoot = CoveRoot, InstanceRoot = "/data" }]);

        var line = Assert.Single(view.Roots);
        Assert.Equal(CoveRoot, line.Root);
        Assert.Equal("/data", line.Mapping);
        Assert.Empty(line.PathsTried);
    }

    /// <summary>A store holding neither a refusal nor a path reads as no lines at all.</summary>
    [Fact]
    public void AStoreHoldingNeitherARefusalNorAPathReadsAsNoLines()
        => Assert.Empty(FolderAgreementView.From([], []).Roots);

    /// <summary>A refused root reads before a working one, whichever way round they are stored.</summary>
    /// <remarks>
    /// A page already showing refusals keeps them where the reader last saw them when a root below
    /// starts working.
    /// </remarks>
    [Fact]
    public void ARefusedRootReadsBeforeAWorkingOne()
    {
        var view = FolderAgreementView.From(
            [
                new OutboundRootRefusal
                {
                    Root = "/shared",
                    Refusal = FolderAgreementRefusal.InstanceDeclaresNoRoot,
                },
            ],
            [new OutboundRootMapping { CoveRoot = CoveRoot, InstanceRoot = "/data" }]);

        Assert.Equal(["/shared", CoveRoot], view.Roots.Select(line => line.Root));
        Assert.Equal(FolderAgreementRefusal.InstanceDeclaresNoRoot, view.Roots[0].Refusal);
    }

    /// <summary>The mapping in force travels beside the root it was supplied for.</summary>
    /// <remarks>
    /// A root with a mapping stored can still be refused: the instance's answer decides on every
    /// run, so the operator has to see what was supplied beside what it came to. The two are one
    /// line even where the spellings differ by a trailing separator.
    /// </remarks>
    [Fact]
    public void TheMappingInForceTravelsBesideTheRootItWasSuppliedFor()
    {
        var view = FolderAgreementView.From(
            [
                new OutboundRootRefusal
                {
                    Root = CoveRoot,
                    Refusal = FolderAgreementRefusal.NothingResolved,
                    PathsTried = ["/gone/Blue Harbor/a.mp4"],
                },
            ],
            [new OutboundRootMapping { CoveRoot = CoveRoot + "/", InstanceRoot = "/gone" }]);

        var line = Assert.Single(view.Roots);
        Assert.Equal("/gone", line.Mapping);
        Assert.Equal(FolderAgreementRefusal.NothingResolved, line.Refusal);
    }

    /// <summary>A refusal naming no root at all has no line.</summary>
    /// <remarks>
    /// A folder under none of the library roots is refused with an empty root. A line for it would
    /// ask for a path for nothing, and the only answer a save could give is that the empty root is
    /// not a library root.
    /// </remarks>
    [Fact]
    public void ARefusalNamingNoRootHasNoLine()
    {
        var view = FolderAgreementView.From(
            [
                new OutboundRootRefusal
                {
                    Root = "",
                    Refusal = FolderAgreementRefusal.FolderUnderNoLibraryRoot,
                },
                new OutboundRootRefusal
                {
                    Root = CoveRoot,
                    Refusal = FolderAgreementRefusal.NothingResolved,
                    PathsTried = ["/data/Blue Harbor/a.mp4"],
                },
            ],
            []);

        var line = Assert.Single(view.Roots);
        Assert.Equal(CoveRoot, line.Root);
        Assert.Equal(FolderAgreementRefusal.NothingResolved, line.Refusal);
        Assert.Equal(["/data/Blue Harbor/a.mp4"], line.PathsTried);
    }

    /// <summary>One refusal a run reported, carrying the paths it asked the instance about.</summary>
    private static FolderAddressRefusal Refused(
        string coveRoot, FolderAgreementRefusal refusal, params string[] tried)
        => new(coveRoot, refusal, tried);

    /// <summary>An options record with both outbound collections holding entries.</summary>
    private static WhisparrSyncOptions Populated()
        => new()
        {
            OutboundRefusals =
            [
                new OutboundRootRefusal
                {
                    Root = CoveRoot,
                    Refusal = FolderAgreementRefusal.NothingResolved,
                    PathsTried = ["/data/Blue Harbor/a.mp4", "/mnt/Blue Harbor/a.mp4"],
                },
                new OutboundRootRefusal
                {
                    Root = "/shared",
                    Refusal = FolderAgreementRefusal.InstanceCannotBeAsked,
                },
            ],
            OutboundMappings =
            [
                new OutboundRootMapping { CoveRoot = CoveRoot, InstanceRoot = "/data" },
            ],
        };
}
