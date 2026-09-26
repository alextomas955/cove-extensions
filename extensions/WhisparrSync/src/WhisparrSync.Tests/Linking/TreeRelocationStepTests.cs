using WhisparrSync.Contracts;
using WhisparrSync.Linking;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Linking;

public sealed class TreeRelocationStepTests
{
    private const string OldRoot = "/library/rootA";

    private const string NewRoot = "/library/rootB";

    private const string NewFolder = "/library/rootB/.wsync-v3/scene-1";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // Every move the step sends, with what it was sent. Recorded rather than counted: what the
    // caller has to know is that nothing left where nothing should.
    private sealed class RecordingMove(WhisparrResponse? answer)
    {
        internal List<(string Root, string? Folder)> Sent { get; } = [];

        internal Func<string, string?, CancellationToken, Task<WhisparrResponse?>> Act
            => (root, folder, _) =>
            {
                Sent.Add((root, folder));
                return Task.FromResult(answer);
            };
    }

    private static WhisparrResponse Accepted { get; } = new(202, null, string.Empty);

    private static WhisparrResponse Declined { get; } = new(400, null, string.Empty);

    [Fact]
    public async Task AnEntityTheInstanceAlreadyHoldsAtTheIntendedFolderIssuesNothing()
    {
        var move = new RecordingMove(Accepted);

        var relocated = await TreeRelocationStep.RelocateAsync(
            NewFolder, new EntityPlacement(NewRoot, NewFolder), move.Act, TestCt);

        Assert.Equal(RelocationAct.AlreadyThere, relocated.Act);
        Assert.Empty(move.Sent);
    }

    // The instance answers its own spelling of its own filesystem. Compared literally, a folder
    // agreed as one spelling never matches the other, so the correction runs on every run and never
    // converges.
    [Theory]
    [InlineData("/LIBRARY/RootB/.wsync-v3/Scene-1")]
    [InlineData(@"\library\rootB\.wsync-v3\scene-1")]
    [InlineData(NewFolder + "/")]
    public async Task AFolderDifferingOnlyInSpellingOrCaseIssuesNothing(string registeredAt)
    {
        var move = new RecordingMove(Accepted);

        var relocated = await TreeRelocationStep.RelocateAsync(
            registeredAt, new EntityPlacement(NewRoot, NewFolder), move.Act, TestCt);

        Assert.Equal(RelocationAct.AlreadyThere, relocated.Act);
        Assert.Empty(move.Sent);
    }

    [Fact]
    public async Task AnEntityWhoseChosenFolderChangedIsMovedToIt()
    {
        var move = new RecordingMove(Accepted);

        var relocated = await TreeRelocationStep.RelocateAsync(
            OldRoot + "/.wsync-v3/scene-1",
            new EntityPlacement(NewRoot, NewFolder),
            move.Act,
            TestCt);

        Assert.Equal(RelocationAct.Moved, relocated.Act);
        Assert.Equal([(NewRoot, NewFolder)], move.Sent);
    }

    // Where no folder was built there is none to send, and the entity moves onto the root reaching
    // its files: sending a folder nothing holds leaves the instance recording an entry whose files
    // it reports and cannot open.
    [Fact]
    public async Task AnEntityWithNoFolderBuiltForItMovesOntoTheRootAlone()
    {
        var move = new RecordingMove(Accepted);

        var relocated = await TreeRelocationStep.RelocateAsync(
            OldRoot, new EntityPlacement(NewRoot, null), move.Act, TestCt);

        Assert.Equal(RelocationAct.Moved, relocated.Act);
        Assert.Equal([(NewRoot, null)], move.Sent);
    }

    [Fact]
    public async Task AMoveTheInstanceDeclinedIsCountedAsRefusedAndCarriesItsAnswer()
    {
        var move = new RecordingMove(Declined);

        var relocated = await TreeRelocationStep.RelocateAsync(
            OldRoot + "/.wsync-v3/scene-1",
            new EntityPlacement(NewRoot, NewFolder),
            move.Act,
            TestCt);

        Assert.Equal(RelocationAct.Declined, relocated.Act);
        Assert.Same(Declined, relocated.Answer);
    }

    // An answer that did not arrive is not a move that happened.
    [Fact]
    public async Task AMoveNothingAnsweredIsCountedAsRefused()
    {
        var move = new RecordingMove(null);

        var relocated = await TreeRelocationStep.RelocateAsync(
            OldRoot + "/.wsync-v3/scene-1",
            new EntityPlacement(NewRoot, NewFolder),
            move.Act,
            TestCt);

        Assert.Equal(RelocationAct.Declined, relocated.Act);
    }

    // The generation gap, which is a refusal before any request leaves rather than a request that
    // fails.
    [Fact]
    public async Task AGenerationHoldingNoRelocationRoleSendsNothingAndIsCountedAsRefused()
    {
        var relocated = await TreeRelocationStep.RelocateAsync(
            OldRoot + "/.wsync-v3/scene-1",
            new EntityPlacement(NewRoot, NewFolder),
            relocate: null,
            TestCt);

        Assert.Equal(RelocationAct.Declined, relocated.Act);
        Assert.Equal(
            MonitorRefusalKind.CapabilityAbsentOnThisGeneration, relocated.Answer?.Refusal);
    }

    [Theory]
    [InlineData(null, NewRoot)]
    [InlineData(OldRoot, null)]
    public async Task NothingIsSentWhereOneSideOfTheComparisonIsMissing(
        string? registeredAt, string? intendedRoot)
    {
        var move = new RecordingMove(Accepted);

        var relocated = await TreeRelocationStep.RelocateAsync(
            registeredAt,
            new EntityPlacement(intendedRoot, intendedRoot is null ? null : NewFolder),
            move.Act,
            TestCt);

        Assert.Equal(RelocationAct.NothingToCompare, relocated.Act);
        Assert.Empty(move.Sent);
    }
}
