using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
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

    // An entry that accumulated would grow with the runs an operator makes and would report a
    // reason that no longer holds.
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

    // Record equality compares a list by reference, so a round-trip's fresh list is the case a
    // default implementation reports as changed.
    [Fact]
    public async Task ARoundTripThroughTheStoreReturnsAnEqualRecord()
    {
        var store = new FakeStore();
        var saved = Populated();

        await new OptionsStore(store).SaveAsync(saved, TestCt);
        var loaded = await new OptionsStore(store).LoadAsync(TestCt);

        Assert.Equal(saved, loaded);
        Assert.Equal(saved.GetHashCode(), loaded.GetHashCode());
        Assert.Equal(2, loaded.Instance().OutboundRefusals.Count);
        Assert.Equal(2, loaded.Instance().OutboundRefusals[0].PathsTried.Count);
        Assert.Equal("/data", Assert.Single(loaded.Instance().OutboundMappings).InstanceRoot);
    }

    [Fact]
    public async Task TheRefusalReasonRoundTripsAsAString()
    {
        var store = new FakeStore();

        await new OptionsStore(store).SaveAsync(Populated(), TestCt);

        var blob = await store.GetAsync(OptionsStore.Key, TestCt);
        Assert.Contains("\"nothingResolved\"", blob, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordsDifferingOnlyInHowManyEntriesTheyHoldAreNotEqual()
    {
        var saved = Populated();

        Assert.NotEqual(saved, saved.WithInstance(outboundRefusals: [saved.Instance().OutboundRefusals[0]]));
        Assert.NotEqual(saved, saved.WithInstance(outboundMappings: []));
        Assert.NotEqual(
            saved.Instance().OutboundRefusals[0],
            saved.Instance().OutboundRefusals[0] with { PathsTried = [saved.Instance().OutboundRefusals[0].PathsTried[0]] });
    }

    // The accessor is what does this. A property initialiser runs only for an absent key, and a
    // blob naming the member as null binds it as null.
    [Fact]
    public async Task ABlobNamingEitherCollectionAsNullBindsItAsEmpty()
    {
        var store = new FakeStore();
        await store.SetAsync(
            OptionsStore.Key,
            """{"instanceSettingsV3":{"outboundRefusals":null,"outboundMappings":null}}""",
            TestCt);

        var loaded = await new OptionsStore(store).LoadAsync(TestCt);

        Assert.Empty(loaded.Instance().OutboundRefusals);
        Assert.Empty(loaded.Instance().OutboundMappings);
    }

    // The outer non-null restore does not descend into a collection's elements.
    [Fact]
    public async Task ABlobNamingOneEntrysPathsAsNullBindsThemAsEmpty()
    {
        var store = new FakeStore();
        await store.SetAsync(
            OptionsStore.Key,
            """
            {"instanceSettingsV3":{"outboundRefusals":[
              {"root":"/shared","refusal":"nothingResolved","pathsTried":null}]}}
            """,
            TestCt);

        var loaded = await new OptionsStore(store).LoadAsync(TestCt);

        Assert.Empty(Assert.Single(loaded.Instance().OutboundRefusals).PathsTried);
    }

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

    // A root that vanished the moment its path worked could not be reviewed or withdrawn through
    // the product at all.
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

    [Fact]
    public void AStoreHoldingNeitherARefusalNorAPathReadsAsNoLines()
        => Assert.Empty(FolderAgreementView.From([], []).Roots);

    // A page already showing refusals keeps them where the reader last saw them when a root below
    // starts working.
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

    // A root with a mapping stored can still be refused, because the instance's answer decides on
    // every run. The two are one line even where the spellings differ by a trailing separator.
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

    // A folder under none of the library roots is refused with an empty root. A line for it would
    // ask for a path for nothing.
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

    private static FolderAddressRefusal Refused(
        string coveRoot, FolderAgreementRefusal refusal, params string[] tried)
        => new(coveRoot, refusal, tried);

    private static WhisparrSyncOptions Populated()
        => new WhisparrSyncOptions().WithInstance(
            outboundRefusals:
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
            outboundMappings:
            [
                new OutboundRootMapping { CoveRoot = CoveRoot, InstanceRoot = "/data" },
            ]);
}
