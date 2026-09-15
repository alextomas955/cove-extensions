using WhisparrSync.Addressing;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;

namespace WhisparrSync.Tests.Options;

/// <summary>
/// What a run's answers about the library roots come to once stored: the entries a run writes, the
/// ones it clears, that they survive the blob, and what the settings page reads off them.
/// </summary>
/// <remarks>
/// The outbound pair mirrors the import refusals: one entry per root rather than per folder, so a
/// library of any size leaves these the same length.
/// </remarks>
public sealed class FolderMappingOptionsTests
{
    private const string CoveRoot = "G:/Downloads/P";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>A run that could not address a root records why, and what it asked about.</summary>
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

    /// <summary>
    /// A second run over one root replaces that root's entry rather than appending to it.
    /// </summary>
    /// <remarks>
    /// The reason and the paths are what the LAST run established. An entry that accumulated would
    /// grow with the runs an operator makes and would report a reason that no longer holds.
    /// </remarks>
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

    /// <summary>A root a run addressed loses its entry, and the other roots keep theirs.</summary>
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

    /// <summary>
    /// Two spellings of one root differing only by a trailing separator are one entry.
    /// </summary>
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

    /// <summary>The paths one entry keeps are bounded by the named constant.</summary>
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

    /// <summary>
    /// A mapping's two roots are both normalised, so a path typed with a trailing separator keys and
    /// rebuilds the same way one typed without it does.
    /// </summary>
    [Fact]
    public void AMappingsTwoRootsAreBothNormalised()
    {
        var mapping = new OutboundRootMapping { CoveRoot = CoveRoot + "/", InstanceRoot = "/data/" };

        Assert.Equal(CoveRoot, mapping.CoveRoot);
        Assert.Equal("/data", mapping.InstanceRoot);
    }

    /// <summary>
    /// A save stores one mapping per root, replaces the one already stored, and a blank path removes
    /// it.
    /// </summary>
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

    /// <summary>Nothing is projected for a root the instance could see.</summary>
    [Fact]
    public void NothingIsProjectedForARootTheInstanceCouldSee()
    {
        Assert.Empty(
            FolderAgreementView.From([], [new OutboundRootMapping
            {
                CoveRoot = CoveRoot,
                InstanceRoot = "/data",
            }]).Roots);
    }

    /// <summary>The mapping in force travels beside the root it was supplied for.</summary>
    /// <remarks>
    /// A root with a mapping stored can still be refused: the instance's answer decides on every
    /// run, so the operator has to see what was supplied beside what it came to.
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

        Assert.Equal("/gone", Assert.Single(view.Roots).Mapping);
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
