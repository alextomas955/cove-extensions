using WhisparrSync.Addressing;
using WhisparrSync.Import;

namespace WhisparrSync.Tests.Addressing;

/// <summary>
/// What one Cove library root and the roots an instance declares make of each other, given one
/// sample file and what the instance reported at each candidate.
/// </summary>
/// <remarks>
/// Pure arithmetic over supplied readings. Whether a candidate really holds that file is a separate
/// reading taken through the instance and folded back in here, exactly as the inbound direction
/// folds a filesystem probe back in.
/// </remarks>
public sealed class FolderAgreementTests
{
    private const string CoveRoot = "G:/Downloads/P";

    private const string Sample = "G:/Downloads/P/Blue Harbor/scene.mp4";

    private const long SampleSize = 41;

    private static readonly string[] TwoInstanceRoots = ["/data", "/media"];

    /// <summary>
    /// One candidate per declared root, and the library's own spelling beside them.
    /// </summary>
    /// <remarks>
    /// The library's own spelling is the deployment where both systems reach one filesystem at one
    /// path, and the one where the instance's root sits below the Cove root. Neither produces a
    /// rebuilt candidate that names the file.
    /// </remarks>
    [Fact]
    public void ASampleFileProducesOneCandidatePerDeclaredRootAndTheLibrarysOwnSpelling()
    {
        var reading = FolderAgreement.CandidatesFor(Sample, CoveRoot, TwoInstanceRoots, mapping: null);

        Assert.Null(reading.Refusal);
        Assert.Equal(
            ["/data/Blue Harbor/scene.mp4", "/media/Blue Harbor/scene.mp4", Sample],
            reading.Candidates);
    }

    /// <summary>
    /// An instance whose root sits below the Cove root agrees on the Cove root itself.
    /// </summary>
    /// <remarks>
    /// One filesystem both systems reach at one path, with the instance's catalogue rooted inside
    /// the library. Rebuilding the tail under the instance's own root repeats the segments that root
    /// already carries, so the only candidate naming the file is the library's own spelling.
    /// </remarks>
    [Fact]
    public void AnInstanceRootedInsideTheLibraryAgreesOnTheLibrarysOwnSpelling()
    {
        const string oneRoot = "/shared";
        const string sample = "/shared/media/Blue Harbor/scene.mp4";
        var reading = FolderAgreement.CandidatesFor(sample, oneRoot, ["/shared/media"], mapping: null);

        // The rebuild repeats the root's own segment and names nothing; the library's own spelling is
        // the file.
        Assert.Equal(["/shared/media/media/Blue Harbor/scene.mp4", sample], reading.Candidates);

        var agreement = FolderAgreement.Resolve(
            sample,
            oneRoot,
            [
                new ProbedCandidate(reading.Candidates[0], new ProbedPath(false, null)),
                new ProbedCandidate(sample, new ProbedPath(true, SampleSize)),
            ],
            SampleSize);

        Assert.Null(agreement.Refusal);
        Assert.Equal(oneRoot, agreement.InstanceRoot);
        Assert.Equal(
            "/shared/media/Blue Harbor",
            FolderAgreement.Address("/shared/media/Blue Harbor", oneRoot, agreement.InstanceRoot!));
    }

    [Fact]
    public void AnInstanceDeclaringNoRootAnswersItsOwnReasonWithNothingTried()
    {
        var reading = FolderAgreement.CandidatesFor(Sample, CoveRoot, [], mapping: null);

        Assert.Equal(FolderAgreementRefusal.InstanceDeclaresNoRoot, reading.Refusal);
        Assert.Empty(reading.Candidates);
    }

    [Fact]
    public void NoFileToProbeWithAnswersItsOwnReason()
    {
        var reading = FolderAgreement.CandidatesFor(null, CoveRoot, TwoInstanceRoots, mapping: null);

        Assert.Equal(FolderAgreementRefusal.NoFileToProbeWith, reading.Refusal);
        Assert.Empty(reading.Candidates);
    }

    /// <summary>
    /// The agreement is the verified candidate with the sample file's own tail removed.
    /// </summary>
    /// <remarks>
    /// Taken off the candidate rather than by searching the declared roots for one that is a prefix,
    /// so instance roots that nest need no tie-break.
    /// </remarks>
    [Fact]
    public void AVerifiedCandidateOfTheRightSizeAgreesOnItsOwnRoot()
    {
        var agreement = FolderAgreement.Resolve(
            Sample,
            CoveRoot,
            [
                Found("/data/Blue Harbor/scene.mp4", SampleSize),
                Absent("/media/Blue Harbor/scene.mp4"),
            ],
            SampleSize);

        Assert.Null(agreement.Refusal);
        Assert.Equal("/data", agreement.InstanceRoot);
    }

    [Fact]
    public void ACandidateReportingNoFileDoesNotResolve()
    {
        var agreement = FolderAgreement.Resolve(
            Sample, CoveRoot, [Absent("/data/Blue Harbor/scene.mp4")], SampleSize);

        Assert.Equal(FolderAgreementRefusal.NothingResolved, agreement.Refusal);
        Assert.Equal(["/data/Blue Harbor/scene.mp4"], agreement.Tried);
        Assert.Null(agreement.InstanceRoot);
    }

    [Fact]
    public void ACandidateOfADifferentSizeDoesNotResolve()
    {
        var agreement = FolderAgreement.Resolve(
            Sample, CoveRoot, [Found("/data/Blue Harbor/scene.mp4", SampleSize + 1)], SampleSize);

        Assert.Equal(FolderAgreementRefusal.NothingResolved, agreement.Refusal);
    }

    /// <summary>Two candidates the sizes cannot separate resolve to nothing, and neither is chosen.</summary>
    [Fact]
    public void TwoCandidatesThatBothResolveChooseNeither()
    {
        var agreement = FolderAgreement.Resolve(
            Sample,
            CoveRoot,
            [
                Found("/data/Blue Harbor/scene.mp4", SampleSize),
                Found("/media/Blue Harbor/scene.mp4", SampleSize),
            ],
            SampleSize);

        Assert.Equal(FolderAgreementRefusal.MoreThanOneResolved, agreement.Refusal);
        Assert.Null(agreement.InstanceRoot);
        Assert.Equal(
            ["/data/Blue Harbor/scene.mp4", "/media/Blue Harbor/scene.mp4"], agreement.Tried);
    }

    [Fact]
    public void AFolderUnderTheAgreedRootIsAddressedByItsTail()
        => Assert.Equal(
            "/data/Blue Harbor", FolderAgreement.Address("G:\\Downloads\\P\\Blue Harbor", CoveRoot, "/data"));

    [Fact]
    public void AFolderOutsideTheAgreedRootIsAddressedByNothing()
        => Assert.Null(FolderAgreement.Address("H:/Elsewhere/Blue Harbor", CoveRoot, "/data"));

    private static ProbedCandidate Found(string path, long size)
        => new(path, new ProbedPath(true, size));

    private static ProbedCandidate Absent(string path) => new(path, new ProbedPath(false, null));
}
