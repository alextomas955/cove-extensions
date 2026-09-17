using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Monitoring;

namespace WhisparrSync.Tests.Jobs;

/// <summary>
/// The one line a run is reported on when it could not address the folders it was given.
/// </summary>
/// <remarks>
/// A run that reached the instance for nothing has both counts at zero, and "0 linked, 0 refused."
/// reads as a clean pass over every folder. This line is the only place the run is reported at all,
/// so the reason has to be in it.
/// <para>
/// Every expected sentence is transcribed by hand. Composing one from the member under test would
/// agree with a sentence that changed underneath it.
/// </para>
/// </remarks>
public sealed class ReflectOwnedSummaryTests
{
    /// <summary>The library root as Cove has it, on a machine the instance does not share.</summary>
    private const string CoveRoot = "G:/Downloads/P";

    /// <summary>The one path under it the instance was asked about.</summary>
    private const string Tried = "/data/Blue Harbor/scene 1.mp4";

    private const string Under = "Nothing under " + CoveRoot + " could be linked: ";

    [Fact]
    public void ARunThatAddressedNothingReportsTheReasonRatherThanACountOfZero()
    {
        var line = ReflectOwnedJob.SummaryOf(
            Run(0, 0, Refused(FolderAgreementRefusal.NothingResolved)));

        Assert.Equal(Under + "Whisparr holds nothing at " + Tried + ".", line);
        Assert.DoesNotContain("0 linked", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunThatLinkedSomeAndCouldNotAddressTheRestReportsBoth()
    {
        var line = ReflectOwnedJob.SummaryOf(
            Run(2, 1, Refused(FolderAgreementRefusal.NothingResolved)));

        Assert.Equal(
            "2 linked, 1 refused. " + Under + "Whisparr holds nothing at " + Tried + ".", line);
    }

    /// <summary>A run with nothing it could not address reports what it has always reported.</summary>
    [Fact]
    public void ARunThatAddressedEveryFolderKeepsItsCounts()
        => Assert.Equal("0 linked, 0 refused.", ReflectOwnedJob.SummaryOf(Run(0, 0)));

    [Fact]
    public void ARunStoppedByTheInstancesLinkingSettingKeepsItsOwnSentence()
    {
        var run = new ReflectOwnedRun(
            ReflectOwnedRunOutcome.Completed, 0, 0, ReflectOwnedSkipReason.HardLinksOff);

        Assert.Equal(
            "No files were linked: Whisparr's hard-link setting is off.",
            ReflectOwnedJob.SummaryOf(run));
    }

    /// <summary>
    /// Each root is named once and carries at most one of the paths tried under it. The roots are an
    /// operator's own small set; the paths under them are not.
    /// </summary>
    [Fact]
    public void ARunReportingTwoRootsNamesEachOnceAndOnePathUnderEach()
    {
        var line = ReflectOwnedJob.SummaryOf(Run(
            0,
            0,
            new FolderAddressRefusal(
                CoveRoot,
                FolderAgreementRefusal.NothingResolved,
                [Tried, "/data/Blue Harbor/second.mp4"]),
            new FolderAddressRefusal(
                "H:/Second", FolderAgreementRefusal.InstanceDeclaresNoRoot, [])));

        Assert.Equal(
            Under + "Whisparr holds nothing at " + Tried + ". "
                + "Nothing under H:/Second could be linked: "
                + "Whisparr declares no root folder to build a path under.",
            line);
        Assert.DoesNotContain("second.mp4", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A library root holding no file to establish the agreement from is not a misconfiguration, so
    /// its sentence asks the reader for nothing.
    /// </summary>
    [Theory]
    [InlineData(
        FolderAgreementRefusal.NothingResolved,
        "Whisparr holds nothing at " + Tried + ".")]
    [InlineData(
        FolderAgreementRefusal.MoreThanOneResolved,
        "Whisparr holds a file of that size at more than one of the paths asked about, including "
            + Tried + ".")]
    [InlineData(
        FolderAgreementRefusal.InstanceDeclaresNoRoot,
        "Whisparr declares no root folder to build a path under.")]
    [InlineData(
        FolderAgreementRefusal.InstanceCannotBeAsked,
        "Whisparr could not be asked what it holds.")]
    [InlineData(
        FolderAgreementRefusal.NoFileToProbeWith,
        "Cove holds no file under it to establish Whisparr's spelling of it from.")]
    [InlineData(
        FolderAgreementRefusal.ProbeCouldNotBeRead,
        "Whisparr's answer about " + Tried + " could not be read.")]
    [InlineData(
        FolderAgreementRefusal.FolderUnderNoLibraryRoot,
        "Cove holds these folders under none of its library paths.")]
    public void EachReasonAFolderCannotBeAddressedHasItsOwnSentence(
        FolderAgreementRefusal refusal, string because)
        => Assert.Equal(Under + because, ReflectOwnedJob.SummaryOf(Run(0, 0, Refused(refusal))));

    /// <summary>A reason with no sentence written down for it throws rather than shipping silently.</summary>
    [Fact]
    public void NoReasonIsLeftWithoutASentence()
    {
        foreach (var refusal in Enum.GetValues<FolderAgreementRefusal>())
        {
            Assert.False(
                string.IsNullOrWhiteSpace(
                    ReflectOwnedJob.SummaryOf(Run(0, 0, Refused(refusal)))));
        }
    }

    /// <summary>
    /// An instance holding no filesystem role answers for every folder at once, so there is no one
    /// library root to name.
    /// </summary>
    [Fact]
    public void ARunOnAnInstanceThatCannotBeAskedNamesNoLibraryRoot()
    {
        var line = ReflectOwnedJob.SummaryOf(Run(
            0,
            0,
            new FolderAddressRefusal(
                string.Empty, FolderAgreementRefusal.InstanceCannotBeAsked, [])));

        Assert.Equal("Nothing could be linked: Whisparr could not be asked what it holds.", line);
    }

    [Fact]
    public void AStoppedRunThatAddressedNothingStillSaysItWasStopped()
    {
        var run = new ReflectOwnedRun(
            ReflectOwnedRunOutcome.Cancelled,
            0,
            0,
            null,
            1,
            [Refused(FolderAgreementRefusal.NothingResolved)]);

        Assert.Contains("then stopped", ReflectOwnedJob.SummaryOf(run), StringComparison.Ordinal);
    }

    /// <summary>
    /// A run that linked nothing because every file's site sits under another root says so, rather
    /// than reporting a pair of zeros.
    /// </summary>
    [Fact]
    public void ARunThatLeftEveryFileUnderAnotherRootReportsTheReasonRatherThanACountOfZero()
    {
        var line = ReflectOwnedJob.SummaryOf(LeftUnderAnotherRoot(0, 0, 3));

        Assert.Equal(
            "Some files were not linked: Whisparr holds their site under a different root from the "
                + "files, and nothing was copied.",
            line);
        Assert.DoesNotContain("0 linked", line, StringComparison.Ordinal);
    }

    /// <summary>A run that linked some and left others reports the counts first and the reason after.</summary>
    [Fact]
    public void ARunThatLinkedSomeAndLeftOthersUnderAnotherRootReportsBoth()
        => Assert.Equal(
            "2 linked, 1 refused. Some files were not linked: Whisparr holds their site under a "
                + "different root from the files, and nothing was copied.",
            ReflectOwnedJob.SummaryOf(LeftUnderAnotherRoot(2, 1, 4)));

    /// <summary>
    /// A run that left nothing out and could address nothing keeps the sentence it already had, so
    /// the new clause is never composed onto a run it is not about.
    /// </summary>
    [Fact]
    public void ARunThatLeftNothingUnderAnotherRootKeepsTheSentenceItAlreadyHad()
    {
        var line = ReflectOwnedJob.SummaryOf(LeftUnderAnotherRoot(0, 0, 0));

        Assert.Equal("0 linked, 0 refused.", line);
        Assert.DoesNotContain("different root", line, StringComparison.Ordinal);
    }

    /// <summary>A run that could address nothing AND left files out reports both reasons.</summary>
    [Fact]
    public void ARunWithBothReasonsReportsTheRootItCouldNotAddressAndThenWhatItLeftOut()
        => Assert.Equal(
            Under + "Whisparr holds nothing at " + Tried + ". "
                + "Some files were not linked: Whisparr holds their site under a different root "
                + "from the files, and nothing was copied.",
            ReflectOwnedJob.SummaryOf(
                new ReflectOwnedRun(
                    ReflectOwnedRunOutcome.Completed,
                    0,
                    0,
                    null,
                    1,
                    [Refused(FolderAgreementRefusal.NothingResolved)],
                    null,
                    2)));

    private static ReflectOwnedRun LeftUnderAnotherRoot(int attached, int refused, int left)
        => new(ReflectOwnedRunOutcome.Completed, attached, refused, null, 0, null, null, left);

    private static FolderAddressRefusal Refused(FolderAgreementRefusal refusal)
        => new(CoveRoot, refusal, [Tried]);

    private static ReflectOwnedRun Run(
        int attached, int refused, params FolderAddressRefusal[] unaddressed)
        => new(
            ReflectOwnedRunOutcome.Completed,
            attached,
            refused,
            null,
            unaddressed.Length,
            unaddressed);
}
