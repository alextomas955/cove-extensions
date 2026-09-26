using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Monitoring;

namespace WhisparrSync.Tests.Jobs;

// A run that reached the instance for nothing has both counts at zero, and "0 recorded by Whisparr, 0 refused."
// reads as a clean pass over every folder. This line is the only place the run is reported, so the
// reason has to be in it. Every expected sentence is transcribed by hand: one composed from the
// member under test would agree with a sentence that changed underneath it.
public sealed class ReflectOwnedSummaryTests
{
    // Cove's root and the path the instance was asked about are spelled for different machines, so
    // a summary that echoed one for the other is visible here.
    private const string CoveRoot = "G:/Downloads/P";

    private const string Tried = "/data/Blue Harbor/scene 1.mp4";

    private const string Under = "Nothing under " + CoveRoot + " could be handed to Whisparr: ";

    [Fact]
    public void ARunThatAddressedNothingReportsTheReasonRatherThanACountOfZero()
    {
        var line = ReflectOwnedJob.SummaryOf(
            Run(0, 0, Refused(FolderAgreementRefusal.NothingResolved)));

        Assert.Equal(Under + "Whisparr holds nothing at " + Tried + ".", line);
        Assert.DoesNotContain("0 recorded", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunThatLinkedSomeAndCouldNotAddressTheRestReportsBoth()
    {
        var line = ReflectOwnedJob.SummaryOf(
            Run(2, 1, Refused(FolderAgreementRefusal.NothingResolved)));

        Assert.Equal(
            "2 recorded by Whisparr, 1 refused. " + Under + "Whisparr holds nothing at " + Tried + ".", line);
    }

    [Fact]
    public void ARunThatAddressedEveryFolderKeepsItsCounts()
        => Assert.Equal("0 recorded by Whisparr, 0 refused.", ReflectOwnedJob.SummaryOf(Run(0, 0)));

    [Fact]
    public void ARunStoppedByTheInstancesLinkingSettingKeepsItsOwnSentence()
    {
        var run = new ReflectOwnedRun(
            ReflectOwnedRunOutcome.Completed, 0, 0, ReflectOwnedSkipReason.HardLinksOff);

        Assert.Equal(
            "No files were handed to Whisparr: its hard-link setting is off.",
            ReflectOwnedJob.SummaryOf(run));
    }

    // The two sentences a reader acts on, transcribed rather than composed. One names where to turn
    // renaming off; the other says the setting was not read, which is a different fact from it being
    // off.
    [Theory]
    [InlineData(
        ReflectOwnedSkipReason.RenamingOn,
        "No files were handed to Whisparr: it is set to rename files. Turn renaming off in "
            + "Whisparr's Settings, Media Management.")]
    [InlineData(
        ReflectOwnedSkipReason.RenameSettingUnreadable,
        "No files were handed to Whisparr: its rename setting could not be read.")]
    public void ARunStoppedByTheInstancesRenamingStatesWhatToChange(
        ReflectOwnedSkipReason reason, string sentence)
        => Assert.Equal(
            sentence,
            ReflectOwnedJob.SummaryOf(
                new ReflectOwnedRun(ReflectOwnedRunOutcome.Completed, 0, 0, reason)));

    // The sentences are written down one per reason and thrown for otherwise, so a reason added
    // without one reaches a reader as an exception in the middle of a run.
    [Fact]
    public void EveryReasonTheServerCanAnswerHasASentence()
        => Assert.All(
            Enum.GetValues<ReflectOwnedSkipReason>(),
            reason => Assert.NotEmpty(ReflectOwnedJob.SentenceFor(reason)));

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
                + "Nothing under H:/Second could be handed to Whisparr: "
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
    // root to name. A library run links most of its folders and still meets folders under no
    // library root, so the refusal names no root: read as a statement about the run it would
    // contradict the count in front of it.
    [Fact]
    public void ARunThatLinkedFilesDoesNotThenSayNothingCouldBeLinked()
    {
        var line = ReflectOwnedJob.SummaryOf(Run(
            6015,
            0,
            new FolderAddressRefusal(
                string.Empty, FolderAgreementRefusal.FolderUnderNoLibraryRoot, [])));

        Assert.StartsWith("6,015 recorded by Whisparr, 0 refused.", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing could be handed to Whisparr", line, StringComparison.Ordinal);
        Assert.Contains("Some folders were not handed to Whisparr", line, StringComparison.Ordinal);
    }

    // The figure is files. A run linking many files from few folders states the files, because the
    // count beside it is scenes and a reader compares the two.
    [Fact]
    public void TheCountStatesFilesRatherThanTheFoldersTheyCameFrom()
        => Assert.StartsWith(
            "2 recorded by Whisparr,", ReflectOwnedJob.SummaryOf(Run(2, 0)), StringComparison.Ordinal);

    [Fact]
    public void ARunOnAnInstanceThatCannotBeAskedNamesNoLibraryRoot()
    {
        var line = ReflectOwnedJob.SummaryOf(Run(
            0,
            0,
            new FolderAddressRefusal(
                string.Empty, FolderAgreementRefusal.InstanceCannotBeAsked, [])));

        Assert.Equal("Nothing could be handed to Whisparr: Whisparr could not be asked what it holds.", line);
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
            "Some files were not handed to Whisparr: it holds their site under a different "
                + "root from the files, and nothing was copied.",
            line);
        Assert.DoesNotContain("0 recorded", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunThatLinkedSomeAndLeftOthersUnderAnotherRootReportsBoth()
        => Assert.Equal(
            "2 recorded by Whisparr, 1 refused. Some files were not handed to Whisparr: it "
                + "holds their site under a different root from the files, and nothing was copied.",
            ReflectOwnedJob.SummaryOf(LeftUnderAnotherRoot(2, 1, 4)));

    [Fact]
    public void ARunThatLeftNothingUnderAnotherRootKeepsTheSentenceItAlreadyHad()
    {
        var line = ReflectOwnedJob.SummaryOf(LeftUnderAnotherRoot(0, 0, 0));

        Assert.Equal("0 recorded by Whisparr, 0 refused.", line);
        Assert.DoesNotContain("different root", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunWithBothReasonsReportsTheRootItCouldNotAddressAndThenWhatItLeftOut()
        => Assert.Equal(
            Under + "Whisparr holds nothing at " + Tried + ". "
                + "Some files were not handed to Whisparr: it holds their site under a "
                + "different root from the files, and nothing was copied.",
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

    // The figure the whole capability exists to move, beside the one a reader compares it against.
    [Fact]
    public void ARunStatesHowManyEntitiesGotAFolderAndHowManyFilesWereLinked()
    {
        var line = ReflectOwnedJob.SummaryOf(
            Completed with { EntitiesGivenAFolder = 12, LinksMade = 31, FilesAttached = 31 });

        Assert.Equal(
            "12 given a folder of their own, 31 linked, 31 recorded by Whisparr, 0 refused.",
            line);
    }

    // A name taken back freed a file's bytes, so it is stated whatever else the run did.
    [Fact]
    public void ARunThatTookNamesBackSaysHowMany()
        => Assert.Equal(
            "0 recorded by Whisparr, 0 refused, 4 taken back.",
            ReflectOwnedJob.SummaryOf(Completed with { LinksRemoved = 4 }));

    [Fact]
    public void ARunThatLeftNamesWaitingSaysHowMany()
        => Assert.Equal(
            "0 recorded by Whisparr, 0 refused, 2 left until they settle.",
            ReflectOwnedJob.SummaryOf(Completed with { LinksWaiting = 2 }));

    // What was linked before the stop stays linked, so the line reports it and reads as stopped
    // rather than as a run that refused everything.
    [Fact]
    public void ARunStoppedPartWayReportsWhatItDidAndReadsAsStopped()
    {
        var line = ReflectOwnedJob.SummaryOf(
            Completed with
            {
                Outcome = ReflectOwnedRunOutcome.Cancelled,
                EntitiesGivenAFolder = 1,
                LinksMade = 2,
                FilesAttached = 2,
            });

        Assert.Equal(
            "1 given a folder of their own, 2 linked, 2 recorded by Whisparr, 0 refused, "
                + "then stopped.",
            line);
    }

    // One line per library root however many entities sat under it, and each root is named: which
    // one cannot be written inside is the whole of what a reader has to act on.
    [Fact]
    public void ARunThatCouldBuildUnderNoRootNamesEachRootOnce()
    {
        var line = ReflectOwnedJob.SummaryOf(
            Completed with { RootsWithNoTree = ["/data", "/data2"] });

        Assert.Equal(
            "Nothing under /data was given a folder of its own: Cove could not write inside that "
                + "library path. Nothing under /data2 was given a folder of its own: Cove could "
                + "not write inside that library path.",
            line);
    }

    [Fact]
    public void ARootThatRefusedBesideALinkedCountIsStatedBesideIt()
    {
        var line = ReflectOwnedJob.SummaryOf(
            Completed with
            {
                EntitiesGivenAFolder = 1,
                LinksMade = 3,
                FilesAttached = 3,
                RootsWithNoTree = ["/data2"],
            });

        Assert.Equal(
            "1 given a folder of their own, 3 linked, 3 recorded by Whisparr, 0 refused. "
                + "Nothing under /data2 was given a folder of its own: Cove could not write "
                + "inside that library path.",
            line);
    }

    // A hard link cannot cross a filesystem, and the act a caller reaches for instead is a copy. A
    // reader is told rather than left with an entity whose files the instance never records.
    [Fact]
    public void ARunThatMetFilesOnAnotherDriveSaysSoAndSaysNothingWasCopied()
        => Assert.Equal(
            "1 linked, 1 recorded by Whisparr, 0 refused. Some files were not linked: they "
                + "are not on the drive Cove keeps their entity's folder on, and nothing was "
                + "copied.",
            ReflectOwnedJob.SummaryOf(
                Completed with
                {
                    LinksMade = 1,
                    FilesAttached = 1,
                    LinksOnAnotherDevice = 2,
                }));

    private static ReflectOwnedRun Completed { get; } =
        new(ReflectOwnedRunOutcome.Completed, 0, 0);

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
