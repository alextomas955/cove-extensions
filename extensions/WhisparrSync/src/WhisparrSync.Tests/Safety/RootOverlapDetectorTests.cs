using WhisparrSync.Safety;
using WhisparrSync.State;

namespace WhisparrSync.Tests.Safety;

/// <summary>
/// The segment-boundary containment rule <see cref="RootOverlapDetector.Contains"/> holds for the root derivation
/// and the path translation in <c>AddContextResolver</c>: an owned file is routed to the Whisparr root that
/// contains it, and a Cove path prefix is rewritten only when it genuinely encloses the path. The pairwise
/// <see cref="RootOverlapDetector.Detect"/> over it reports every overlapping (Whisparr root, Cove root) pair.
/// </summary>
/// <remarks>
/// Two cases are load-bearing. The sibling-prefix one: were <c>/data/media-evil</c> read as living beneath
/// <c>/data/media</c>, a file on a sibling disk would be registered against the wrong root. And the direction one:
/// containment is tested BOTH ways, because on the live layout the WHISPARR root is the outer path, so a
/// one-direction check would report no overlap where one exists. Callers normalize through
/// <see cref="EventLedger.NormalizePath"/> first, which is what makes the comparison here a plain case-sensitive
/// ordinal one (the Linux/Docker target).
/// </remarks>
[Trait("Tier", "L0")]
public sealed class RootOverlapDetectorTests
{
    [Fact]
    public void IdenticalPaths_AreContained()
    {
        Assert.True(RootOverlapDetector.Contains("/data/media", "/data/media"));
    }

    [Fact]
    public void ChildAtASegmentBoundary_IsContained()
    {
        Assert.True(RootOverlapDetector.Contains("/data/media", "/data/media/movies"));
        Assert.True(RootOverlapDetector.Contains("/data/media", "/data/media/movies/2024/a.mkv"));
    }

    [Fact]
    public void SiblingPrefix_IsNotContained()
    {
        Assert.False(RootOverlapDetector.Contains("/data/media", "/data/media-evil"));
        Assert.False(RootOverlapDetector.Contains("/data/media", "/data/media-evil/a.mkv"));
    }

    [Fact]
    public void AParentIsNotContainedInItsChild()
    {
        Assert.False(RootOverlapDetector.Contains("/data/media/movies", "/data/media"));
    }

    [Fact]
    public void DisjointPaths_AreNotContained()
    {
        Assert.False(RootOverlapDetector.Contains("/data/media", "/srv/archive"));
    }

    [Fact]
    public void ComparisonIsCaseSensitive()
    {
        // On the Linux/Docker target these are different directories. A case-mismatched pair must not
        // resolve to the same root.
        Assert.False(RootOverlapDetector.Contains("/data/media", "/Data/Media/movies"));
    }

    [Fact]
    public void NormalizePath_IsWhatMakesTheOrdinalCompareEnough()
    {
        // Contains requires pre-normalized input; this pins the pairing its callers rely on.
        Assert.True(
            RootOverlapDetector.Contains(
                EventLedger.NormalizePath(@"/data/media\"),
                EventLedger.NormalizePath(@"/data/media\movies\a.mkv")));
    }

    [Fact]
    public void CoveRootNestedInsideWhisparrRoot_ProducesOneOverlap()
    {
        var overlaps = RootOverlapDetector.Detect(
            whisparrRoots: ["/data/media"], coveRoots: ["/data/media/movies"]);

        var overlap = Assert.Single(overlaps);
        Assert.Equal("/data/media", overlap.WhisparrRoot);
        Assert.Equal("/data/media/movies", overlap.CoveRoot);
    }

    [Fact]
    public void WhisparrRootNestedInsideCoveRoot_AlsoWarns()
    {
        var overlaps = RootOverlapDetector.Detect(
            whisparrRoots: ["/data/media/movies"], coveRoots: ["/data/media"]);

        var overlap = Assert.Single(overlaps);
        Assert.Equal("/data/media/movies", overlap.WhisparrRoot);
        Assert.Equal("/data/media", overlap.CoveRoot);
    }

    [Fact]
    public void IdenticalRoots_Warn()
    {
        var overlaps = RootOverlapDetector.Detect(
            whisparrRoots: ["/data/media"], coveRoots: ["/data/media"]);

        Assert.Single(overlaps);
    }

    [Fact]
    public void DisjointRoots_ProduceNoOverlap()
    {
        var overlaps = RootOverlapDetector.Detect(
            whisparrRoots: ["/data/whisparr"], coveRoots: ["/data/cove"]);

        Assert.Empty(overlaps);
    }

    [Fact]
    public void SiblingPrefix_IsNotAnOverlap()
    {
        // "/data/media-evil" must NOT be treated as inside "/data/media" — containment is segment-bounded.
        var overlaps = RootOverlapDetector.Detect(
            whisparrRoots: ["/data/media"], coveRoots: ["/data/media-evil"]);

        Assert.Empty(overlaps);
    }

    [Fact]
    public void ComparisonIsSeparatorNormalized_CaseSensitive()
    {
        // Separators unify (\ → /), so a Windows-style root overlaps its forward-slash child.
        var overlaps = RootOverlapDetector.Detect(
            whisparrRoots: [@"C:\Data\Media"], coveRoots: ["C:/Data/Media/Movies"]);
        Assert.Single(overlaps);

        // But comparison is case-SENSITIVE: a case-mismatched pair is NOT treated as the same root.
        var caseMismatch = RootOverlapDetector.Detect(
            whisparrRoots: [@"C:\Data\Media"], coveRoots: ["c:/data/media/Movies"]);
        Assert.Empty(caseMismatch);
    }

    [Fact]
    public void EmptyOrWhitespaceRoots_AreIgnored()
    {
        var overlaps = RootOverlapDetector.Detect(
            whisparrRoots: ["", "   "], coveRoots: ["/data/media"]);

        Assert.Empty(overlaps);
    }

    [Fact]
    public void NullCollectionOnEitherSide_YieldsNoOverlaps()
    {
        // The documented contract of the method the ingest guard's provider feeds: an unavailable root set is a
        // null/empty collection, and it must degrade to "nothing to report", never to a null dereference.
        Assert.Empty(RootOverlapDetector.Detect(null, ["/data/media"]));
        Assert.Empty(RootOverlapDetector.Detect(["/data/media"], null));
        Assert.Empty(RootOverlapDetector.Detect(null, null));
    }
}
