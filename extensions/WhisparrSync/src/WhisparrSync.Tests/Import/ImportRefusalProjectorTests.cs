using System.Text.Json;
using WhisparrSync.Import;
using WhisparrSync.Options;

namespace WhisparrSync.Tests.Import;

/// <summary>
/// The fold that turns a stream of refusals into a value whose size is the root count, not the
/// library's, and the success that clears one root without touching another.
/// </summary>
public sealed class ImportRefusalProjectorTests
{
    private const string Root = "/whisparr-media";
    private const string Other = "/whisparr-other";

    [Fact]
    public void TheFirstRefusalForARootOpensThatRootsLine()
    {
        var folded = ImportRefusalProjector.Refuse(
            [], Root, "/whisparr-media/one.mp4", ImportRefusalCause.NotFoundUnderAnyRoot);

        var entry = Assert.Single(folded);
        Assert.Equal(Root, entry.Root);
        Assert.Equal(1, entry.CountSinceLastSuccess);
        Assert.Equal(
            [new ImportRefusalEntry
            {
                Path = "/whisparr-media/one.mp4",
                Cause = ImportRefusalCause.NotFoundUnderAnyRoot,
            }],
            entry.NewestPaths);
    }

    /// <summary>Three refusals fill the line; a fourth drops the oldest and leads with the newest.</summary>
    [Fact]
    public void AFourthRefusalDropsTheOldestAndLeadsWithTheNewest()
    {
        var folded = Refusals(4);

        var entry = Assert.Single(folded);
        Assert.Equal(4, entry.CountSinceLastSuccess);
        Assert.Equal(
            ["/whisparr-media/4.mp4", "/whisparr-media/3.mp4", "/whisparr-media/2.mp4"],
            entry.NewestPaths.Select(path => path.Path));
    }

    /// <summary>
    /// A hundred refusals for one root leave the same three paths and one count as four do.
    /// </summary>
    /// <remarks>
    /// The entry's size is what the aggregate promises: whatever a library throws at it, one root's
    /// line is a count and three paths.
    /// </remarks>
    [Fact]
    public void AHundredRefusalsForOneRootStillLeaveThreePathsAndOneCount()
    {
        var entry = Assert.Single(Refusals(100));

        Assert.Equal(100, entry.CountSinceLastSuccess);
        Assert.Equal(ImportRootRefusals.NewestPathsKept, entry.NewestPaths.Count);
        Assert.Equal(
            ["/whisparr-media/100.mp4", "/whisparr-media/99.mp4", "/whisparr-media/98.mp4"],
            entry.NewestPaths.Select(path => path.Path));
    }

    /// <summary>A path already listed is already reported.</summary>
    [Fact]
    public void ARepeatedPathNeitherLengthensTheListNorCountsTwice()
    {
        var once = ImportRefusalProjector.Refuse(
            [], Root, "/whisparr-media/one.mp4", ImportRefusalCause.NotFoundUnderAnyRoot);
        var twice = ImportRefusalProjector.Refuse(
            once, Root, "/whisparr-media/one.mp4", ImportRefusalCause.NotFoundUnderAnyRoot);

        var entry = Assert.Single(twice);
        Assert.Equal(1, entry.CountSinceLastSuccess);
        Assert.Single(entry.NewestPaths);

        // The whole aggregate is unchanged, which is the reading the caller skips a write on.
        Assert.Equal(once, twice);
    }

    /// <summary>The same path refused for a different reason carries the newer reason.</summary>
    /// <remarks>
    /// Still one entry and still one count: the delivery said something new about a path the root
    /// already lists, not that the root failed again.
    /// </remarks>
    [Fact]
    public void ARepeatedPathWithANewCauseCarriesTheNewCause()
    {
        var once = ImportRefusalProjector.Refuse(
            [], Root, "/whisparr-media/one.mp4", ImportRefusalCause.NotFoundUnderAnyRoot);
        var again = ImportRefusalProjector.Refuse(
            once, Root, "/whisparr-media/one.mp4", ImportRefusalCause.AmbiguousCandidates);

        var entry = Assert.Single(again);
        Assert.Equal(1, entry.CountSinceLastSuccess);
        Assert.Equal(
            ImportRefusalCause.AmbiguousCandidates,
            Assert.Single(entry.NewestPaths).Cause);
    }

    /// <summary>Each listed path keeps the cause it was refused for.</summary>
    [Fact]
    public void EachListedPathKeepsItsOwnCause()
    {
        var folded = ImportRefusalProjector.Refuse(
            ImportRefusalProjector.Refuse(
                [], Root, "/whisparr-media/missing.mp4", ImportRefusalCause.NotFoundUnderAnyRoot),
            Root,
            "/whisparr-media/twice.mp4",
            ImportRefusalCause.AmbiguousCandidates);

        Assert.Equal(
            [ImportRefusalCause.AmbiguousCandidates, ImportRefusalCause.NotFoundUnderAnyRoot],
            Assert.Single(folded).NewestPaths.Select(path => path.Cause));
    }

    /// <summary>Two spellings of one root differing only by a trailing separator are one entry.</summary>
    [Theory]
    [InlineData("/whisparr-media/")]
    [InlineData("/whisparr-media\\")]
    public void TwoSpellingsOfOneRootFoldIntoOneEntry(string trailing)
    {
        var folded = ImportRefusalProjector.Refuse(
            ImportRefusalProjector.Refuse(
                [], Root, "/whisparr-media/one.mp4", ImportRefusalCause.NotFoundUnderAnyRoot),
            trailing,
            "/whisparr-media/two.mp4",
            ImportRefusalCause.NotFoundUnderAnyRoot);

        var entry = Assert.Single(folded);
        Assert.Equal(Root, entry.Root);
        Assert.Equal(2, entry.CountSinceLastSuccess);
    }

    /// <summary>A delivery whose path fell under no root is counted, not dropped.</summary>
    [Fact]
    public void ADeliveryUnderNoRootLandsUnderTheStatedPlaceholder()
    {
        var folded = ImportRefusalProjector.Refuse(
            [],
            ImportRefusalProjector.NoReportedRoot,
            "/elsewhere/one.mp4",
            ImportRefusalCause.NotFoundUnderAnyRoot);

        var entry = Assert.Single(folded);
        Assert.Equal(ImportRefusalProjector.NoReportedRoot, entry.Root);
        Assert.Equal(1, entry.CountSinceLastSuccess);
    }

    /// <summary>A root's success clears its own line and leaves every other root's alone.</summary>
    [Fact]
    public void ASuccessClearsOneRootAndLeavesAnotherIntact()
    {
        var before = ImportRefusalProjector.Refuse(
            Refusals(2), Other, "/whisparr-other/one.mp4", ImportRefusalCause.AmbiguousCandidates);

        var after = ImportRefusalProjector.Succeed(before, Root);

        Assert.Equal(Other, Assert.Single(after).Root);
        Assert.Equal(before.Single(entry => entry.Root == Other), after[0]);
    }

    /// <summary>Three roots, one success: the other two lines survive unchanged.</summary>
    [Fact]
    public void ASuccessOnOneOfThreeRootsLeavesTheOtherTwoAsTheyWere()
    {
        var before = ImportRefusalProjector.Refuse(
            ImportRefusalProjector.Refuse(
                Refusals(2), Other, "/whisparr-other/one.mp4", ImportRefusalCause.AmbiguousCandidates),
            "/whisparr-third",
            "/whisparr-third/one.mp4",
            ImportRefusalCause.Unreadable);

        var after = ImportRefusalProjector.Succeed(before, Root);

        Assert.Equal(
            before.Where(entry => entry.Root != Root),
            after);
    }

    [Fact]
    public void ASuccessForARootWithNoLineChangesNothing()
    {
        var before = Refusals(2);

        Assert.Equal(before, ImportRefusalProjector.Succeed(before, Other));
    }

    /// <summary>The count does not wrap when there is no larger one.</summary>
    /// <remarks>
    /// Its control is a count one below the bound, which does move: without it a fold that never
    /// counted at all would satisfy the assertion.
    /// </remarks>
    [Fact]
    public void TheCountAtItsBoundDoesNotBecomeNegative()
    {
        var atBound = ImportRefusalProjector.Refuse(
            [new ImportRootRefusals { Root = Root, CountSinceLastSuccess = int.MaxValue }],
            Root,
            "/whisparr-media/one.mp4",
            ImportRefusalCause.NotFoundUnderAnyRoot);
        Assert.Equal(int.MaxValue, Assert.Single(atBound).CountSinceLastSuccess);

        var belowBound = ImportRefusalProjector.Refuse(
            [new ImportRootRefusals { Root = Root, CountSinceLastSuccess = int.MaxValue - 1 }],
            Root,
            "/whisparr-media/one.mp4",
            ImportRefusalCause.NotFoundUnderAnyRoot);
        Assert.Equal(int.MaxValue, Assert.Single(belowBound).CountSinceLastSuccess);
    }

    /// <summary>The spelling the stored blob carries, which the containerized spec reads by hand.</summary>
    /// <remarks>
    /// The expectation is transcribed rather than computed from the model, so it can disagree with it.
    /// A spec that reads the blob out of Cove's own bulk data route has nothing else to check its
    /// field names and its enum spelling against.
    /// </remarks>
    [Fact]
    public void TheStoredAggregateCarriesTheSpellingTheBannerIsReadBy()
    {
        var stored = JsonSerializer.Serialize(
            new WhisparrSyncOptions
            {
                ImportRefusals = ImportRefusalProjector.Refuse(
                    [], Root, "/whisparr-media/one.mp4", ImportRefusalCause.NotFoundUnderAnyRoot),
            },
            WhisparrSyncOptions.JsonOptions);

        Assert.Contains(
            """
            "ImportRefusals":[{"Root":"/whisparr-media","CountSinceLastSuccess":1,"NewestPaths":[{"Path":"/whisparr-media/one.mp4","Cause":"notFoundUnderAnyRoot"}]}]
            """,
            stored,
            StringComparison.Ordinal);
    }

    private static List<ImportRootRefusals> Refusals(int count)
    {
        List<ImportRootRefusals> folded = [];
        for (var refusal = 1; refusal <= count; refusal++)
        {
            folded = ImportRefusalProjector.Refuse(
                folded,
                Root,
                $"/whisparr-media/{refusal}.mp4",
                ImportRefusalCause.NotFoundUnderAnyRoot);
        }

        return folded;
    }
}
