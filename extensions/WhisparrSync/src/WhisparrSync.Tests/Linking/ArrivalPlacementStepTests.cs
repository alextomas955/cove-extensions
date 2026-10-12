using WhisparrSync.Linking;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Linking;

// Driven against the recording seam rather than a disk, because what is asserted is which
// destinations were attempted and in what order. A filesystem answers those questions and records
// none of them.
public sealed class ArrivalPlacementStepTests
{
    private const string CoveRoot = "/data";
    private const string EntityFolder = CoveRoot + "/.wsync-v3/tt1234567";
    private const string Arrival = EntityFolder + "/Scene.2026.mp4";

    private const string ItemFolder = CoveRoot + "/per-studio/Wicked";
    private const string ItemFile = ItemFolder + "/My First Time.mp4";
    private const string BesideTheItem = ItemFolder + "/Scene.2026.mp4";
    private const string AtTheTopOfTheRoot = CoveRoot + "/Scene.2026.mp4";

    private static readonly IReadOnlyList<string> Roots = ["/data", "/data2"];

    private static readonly FileIdentity TheArrival = new(56, 0x100);
    private static readonly FileIdentity TheItemsFile = new(56, 0x200);
    private static readonly FileIdentity SomethingElse = new(56, 0x300);

    private static readonly DateTimeOffset Changed = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APathOutsideEveryTreeIsNotAnArrivalAndNothingIsAttemptedForIt()
    {
        var port = new RecordingTreeLinkPort();

        var placement = await PlaceAsync(port, ItemFile, heldByTheItem: null);

        Assert.Equal(ItemFile, placement.Path);
        Assert.Empty(port.Calls);
    }

    [Fact]
    public async Task AnArrivalForAnItemTheLibraryHoldsAFileForIsPlacedBesideThatFile()
    {
        var port = Arrived();
        port.Place(ItemFile, TheItemsFile, Changed);
        port.AnswerLink(BesideTheItem, LinkOutcome.Linked);

        var placement = await PlaceAsync(port, Arrival, ItemFile);

        Assert.Equal(BesideTheItem, placement.Path);
        Assert.Equal(new TreeLinkCall("link", BesideTheItem, Arrival), Assert.Single(port.Calls));
    }

    [Fact]
    public async Task AnArrivalForAnItemTheLibraryHoldsNoFileForIsPlacedAtTheTopOfItsRoot()
    {
        var port = Arrived();
        port.AnswerLink(AtTheTopOfTheRoot, LinkOutcome.Linked);

        var placement = await PlaceAsync(port, Arrival, heldByTheItem: null);

        Assert.Equal(AtTheTopOfTheRoot, placement.Path);
    }

    // The verdict is the platform's answer to the attempt, never a comparison of the two paths'
    // declared roots: the item's folder and the arrival sit under one root here and the attempt
    // still refuses, which is the case a root comparison reads as placeable.
    [Fact]
    public async Task ADestinationTheAttemptRefusesAsBeingOnAnotherDeviceFallsBackToTheTopOfTheRoot()
    {
        var port = Arrived();
        port.Place(ItemFile, TheItemsFile, Changed);
        port.AnswerLink(BesideTheItem, LinkOutcome.OnAnotherDevice);
        port.AnswerLink(AtTheTopOfTheRoot, LinkOutcome.Linked);

        var placement = await PlaceAsync(port, Arrival, ItemFile);

        Assert.Equal(AtTheTopOfTheRoot, placement.Path);
        Assert.Equal(
            [
                new TreeLinkCall("link", BesideTheItem, Arrival),
                new TreeLinkCall("link", AtTheTopOfTheRoot, Arrival),
            ],
            port.Calls.Where(call => call.Verb == "link"));
    }

    // The second delivery of one arrival. The name is there and holds the arrival's own bytes,
    // which is the placement already done rather than something in the way of it.
    [Fact]
    public async Task ADestinationAlreadyHoldingTheArrivalIsAnsweredAsThePlacement()
    {
        var port = Arrived();
        port.PlaceLink(AtTheTopOfTheRoot, Arrival);

        var placement = await PlaceAsync(port, Arrival, heldByTheItem: null);

        Assert.Equal(AtTheTopOfTheRoot, placement.Path);
    }

    [Fact]
    public async Task ADestinationHeldByAnotherFileIsRefusedAndNoSecondDestinationIsAttempted()
    {
        var port = Arrived();
        port.Place(ItemFile, TheItemsFile, Changed);
        port.Place(BesideTheItem, SomethingElse, Changed);

        var placement = await PlaceAsync(port, Arrival, ItemFile);

        Assert.Null(placement.Path);
        Assert.Equal([BesideTheItem], port.Calls.Where(call => call.Verb == "link").Select(call => call.Path));
    }

    // An item whose only file is itself inside a tree is the state this capability exists to leave
    // behind, and placing beside it would put the arrival back where no rescan can find it.
    [Fact]
    public async Task AnItemWhoseOwnFileIsInsideATreeIsPlacedAtTheTopOfTheRootInstead()
    {
        var port = Arrived();
        port.Place(EntityFolder + "/Earlier.mp4", TheItemsFile, Changed);
        port.AnswerLink(AtTheTopOfTheRoot, LinkOutcome.Linked);

        var placement = await PlaceAsync(port, Arrival, EntityFolder + "/Earlier.mp4");

        Assert.Equal(AtTheTopOfTheRoot, placement.Path);
    }

    private static RecordingTreeLinkPort Arrived()
    {
        var port = new RecordingTreeLinkPort();
        port.Place(Arrival, TheArrival, Changed);
        return port;
    }

    private static Task<ArrivalPlacement> PlaceAsync(
        RecordingTreeLinkPort port, string arrival, string? heldByTheItem)
        => ArrivalPlacementStep.PlaceAsync(
            arrival, Roots, _ => Task.FromResult(heldByTheItem), port, TestCt);
}
