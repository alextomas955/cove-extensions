using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Monitoring;

namespace WhisparrSync.Tests.Jobs;

// A run that reached the instance for nothing has both counts at zero, and "0 linked, 0 refused."
// reads as a clean pass over every folder. This line is the only place the run is reported, so the
// reason has to be in it. Every expected sentence is transcribed by hand. Composing one from the
// member under test would agree with a sentence that changed underneath it.
public sealed class ReflectOwnedSummaryTests
{
    // Cove's root and the path the instance was asked about are spelled for different machines, so
    // a summary that echoed one for the other is visible here.
    private const string CoveRoot = "G:/Downloads/P";

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

    // A root is an operator's own small set; the paths under it are not, so the line carries at
    // most one path per root.
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

    // An instance that cannot be asked answers for every folder at once, so there is no one library
    // root to name.
    // A library run links most of its folders and still meets folders under no library root. The
    // refusal names no root, and read as a statement about the run it contradicts the count in front
    // of it.
    [Fact]
    public void ARunThatLinkedFilesDoesNotThenSayNothingCouldBeLinked()
    {
        var line = ReflectOwnedJob.SummaryOf(Run(
            6015,
            0,
            new FolderAddressRefusal(
                string.Empty, FolderAgreementRefusal.FolderUnderNoLibraryRoot, [])));

        Assert.StartsWith("6,015 linked, 0 refused.", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing could be linked", line, StringComparison.Ordinal);
        Assert.Contains("Some folders were not linked", line, StringComparison.Ordinal);
    }

    // The figure is files. A run linking many files from few folders states the files, because the
    // count beside it is scenes and a reader compares the two.
    [Fact]
    public void TheCountStatesFilesRatherThanTheFoldersTheyCameFrom()
        => Assert.StartsWith(
            "2 linked,", ReflectOwnedJob.SummaryOf(Run(2, 0)), StringComparison.Ordinal);

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

    [Fact]
    public void ARunThatLinkedSomeAndLeftOthersUnderAnotherRootReportsBoth()
        => Assert.Equal(
            "2 linked, 1 refused. Some files were not linked: Whisparr holds their site under a "
                + "different root from the files, and nothing was copied.",
            ReflectOwnedJob.SummaryOf(LeftUnderAnotherRoot(2, 1, 4)));

    [Fact]
    public void ARunThatLeftNothingUnderAnotherRootKeepsTheSentenceItAlreadyHad()
    {
        var line = ReflectOwnedJob.SummaryOf(LeftUnderAnotherRoot(0, 0, 0));

        Assert.Equal("0 linked, 0 refused.", line);
        Assert.DoesNotContain("different root", line, StringComparison.Ordinal);
    }

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

    // The figure the line states is files. A folder count travels with it because a file is only
    // attached as part of one, and the two are not interchangeable in the sentence.
    private static ReflectOwnedRun LeftUnderAnotherRoot(int filesAttached, int refused, int left)
        => new(
            ReflectOwnedRunOutcome.Completed,
            filesAttached > 0 ? 1 : 0,
            refused,
            EntriesLeftUnderAnotherRoot: left,
            FilesAttached: filesAttached);

    private static FolderAddressRefusal Refused(FolderAgreementRefusal refusal)
        => new(CoveRoot, refusal, [Tried]);

    private static ReflectOwnedRun Run(
        int filesAttached, int refused, params FolderAddressRefusal[] unaddressed)
        => new(
            ReflectOwnedRunOutcome.Completed,
            filesAttached > 0 ? 1 : 0,
            refused,
            FoldersNotAddressed: unaddressed.Length,
            AddressRefusals: unaddressed,
            FilesAttached: filesAttached);
}
