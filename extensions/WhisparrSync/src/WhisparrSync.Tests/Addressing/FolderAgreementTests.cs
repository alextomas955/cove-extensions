using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Import;

namespace WhisparrSync.Tests.Addressing;

// Pure arithmetic over supplied readings. Whether a candidate really holds the file is a separate
// reading taken through the instance and folded back in here.
public sealed class FolderAgreementTests
{
    private const string CoveRoot = "G:/Downloads/P";

    private const string Sample = "G:/Downloads/P/Blue Harbor/scene.mp4";

    private const long SampleSize = 41;

    private static readonly string[] TwoInstanceRoots = ["/data", "/media"];

    // The library's own spelling covers the deployment where both systems reach one filesystem at
    // one path, and the one where the instance's root sits below the Cove root. Neither produces a
    // rebuilt candidate that names the file.
    [Fact]
    public void ASampleFileProducesOneCandidatePerDeclaredRootAndTheLibrarysOwnSpelling()
    {
        var reading = FolderAgreement.CandidatesFor(Sample, CoveRoot, TwoInstanceRoots, mapping: null);

        Assert.Null(reading.Refusal);
        Assert.Equal(
            ["/data/Blue Harbor/scene.mp4", "/media/Blue Harbor/scene.mp4", Sample],
            reading.Candidates);
    }

    // One filesystem both systems reach at one path, with the instance's catalogue rooted inside
    // the library. Rebuilding the tail under the instance's own root repeats the segments that root
    // already carries, so the only candidate naming the file is the library's own spelling.
    [Fact]
    public void AnInstanceRootedInsideTheLibraryAgreesOnTheLibrarysOwnSpelling()
    {
        const string oneRoot = "/shared";
        const string sample = "/shared/media/Blue Harbor/scene.mp4";
        var reading = FolderAgreement.CandidatesFor(sample, oneRoot, ["/shared/media"], mapping: null);

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

    // The root is taken off the verified candidate rather than by searching the declared roots for
    // one that is a prefix, so instance roots that nest need no tie-break.
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

    // The library root itself holds files in plenty of libraries, and it addresses to the instance
    // root: the tail below it is empty rather than absent.
    [Theory]
    [InlineData(@"G:\Downloads\P")]
    [InlineData("G:/Downloads/P")]
    [InlineData("G:/Downloads/P/")]
    public void TheAgreedRootItselfIsAddressedAsTheInstanceRoot(string folder)
        => Assert.Equal("/data", FolderAgreement.Address(folder, CoveRoot, "/data"));

    [Fact]
    public void AFolderOutsideTheAgreedRootIsAddressedByNothing()
        => Assert.Null(FolderAgreement.Address("H:/Elsewhere/Blue Harbor", CoveRoot, "/data"));

    private static ProbedCandidate Found(string path, long size)
        => new(path, new ProbedPath(true, size));

    private static ProbedCandidate Absent(string path) => new(path, new ProbedPath(false, null));
}
