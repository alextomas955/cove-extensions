using WhisparrSync.Import;

namespace WhisparrSync.Linking;

/// <summary>Where the library is to hold a file that arrived inside the tree.</summary>
/// <remarks>
/// A null <see cref="Path"/> is a refusal, and a caller acts on it by registering nothing and
/// leaving the downloaded file where it is.
/// </remarks>
internal sealed record ArrivalPlacement
{
    private ArrivalPlacement(string? path) => Path = path;

    /// <summary>The path to register, or null where the arrival could not be placed.</summary>
    public string? Path { get; }

    /// <summary>Nowhere to place it, with nothing moved, removed or overwritten.</summary>
    internal static ArrivalPlacement Refused { get; } = new((string?)null);

    internal static ArrivalPlacement At(string path) => new(path);
}

/// <summary>
/// Gives a file the instance downloaded into the tree a name in the library the reader keeps.
/// </summary>
/// <remarks>
/// The tree is invisible to the host's own scan by design, so an item whose only file sits there is
/// outside the reader's layout, beyond anything a rescan would find again, and open to being renamed
/// where the instance still records the old path.
/// <para>
/// The tree name is left alone. The instance records the arrival at the tree path, this gives the
/// same bytes a second name in the library, and the two sides pair by identity the way every other
/// file does. The arrival keeps the name the instance gave it: this extension does not name a
/// reader's files.
/// </para>
/// <para>
/// Which device a path is on is read off the attempt and never off a comparison of declared roots.
/// Two paths under one root can sit on two devices, so the platform's refusal is the only reading
/// that is true of the filesystem rather than of the configuration, and the fallback is what
/// follows that reading rather than a prediction made before it.
/// </para>
/// <para>
/// Pure, and holds nothing: one arrival costs at most two link attempts and two identity reads,
/// whatever the item or the library holds.
/// </para>
/// </remarks>
internal static class ArrivalPlacementStep
{
    /// <summary>Where <paramref name="arrivalPath"/> belongs, or that it cannot be placed.</summary>
    /// <remarks>
    /// A path in no tree is answered as itself: it is not an arrival, and the caller registers it
    /// where it already is.
    /// <para>
    /// <paramref name="heldByTheItem"/> reads the file the item this delivery names currently
    /// holds, and is asked only where the answer can change the destination.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="arrivalPath"/> is blank.</exception>
    /// <exception cref="ArgumentNullException">Any other argument is null.</exception>
    internal static async Task<ArrivalPlacement> PlaceAsync(
        string arrivalPath,
        IReadOnlyList<string> coveRoots,
        Func<CancellationToken, Task<string?>> heldByTheItem,
        ITreeLinkPort links,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arrivalPath);
        ArgumentNullException.ThrowIfNull(coveRoots);
        ArgumentNullException.ThrowIfNull(heldByTheItem);
        ArgumentNullException.ThrowIfNull(links);

        if (TreePathGuard.RootOfTreeHolding(arrivalPath, coveRoots) is not { } coveRoot)
        {
            return ArrivalPlacement.At(arrivalPath);
        }

        var name = PathCandidateGuard.LeafOf(arrivalPath);
        var beside = await BesideTheItemsOwnFileAsync(heldByTheItem, coveRoots, name, ct)
            .ConfigureAwait(false);

        foreach (var destination in Destinations(beside, PathCandidateGuard.CandidateUnder(coveRoot, name)))
        {
            ct.ThrowIfCancellationRequested();

            switch (links.Link(arrivalPath, destination))
            {
                case LinkOutcome.Linked:
                    return ArrivalPlacement.At(destination);
                case LinkOutcome.NameAlreadyThere:
                    // The arrival's own bytes under that name is this delivery arriving a second
                    // time. Anything else there is a file of the reader's, and no destination this
                    // step could reach for instead would be less of a guess.
                    return HoldsTheSameFile(links, arrivalPath, destination)
                        ? ArrivalPlacement.At(destination)
                        : ArrivalPlacement.Refused;
                case LinkOutcome.OnAnotherDevice:
                    continue;
                default:
                    return ArrivalPlacement.Refused;
            }
        }

        return ArrivalPlacement.Refused;
    }

    // The item's own folder first, then the top of the root the tree sits under. Distinct, so a
    // fallback onto a destination the first attempt already refused is not attempted twice.
    private static IEnumerable<string> Destinations(string? beside, string? atTheTopOfTheRoot)
        => new[] { beside, atTheTopOfTheRoot }
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);

    // Where the reader already keeps this item, which is where they would look for another of its
    // files. An item whose own file is itself inside a tree is passed over: placing beside it would
    // leave the arrival where no rescan can reach it, which is the state this step exists to end.
    private static async Task<string?> BesideTheItemsOwnFileAsync(
        Func<CancellationToken, Task<string?>> heldByTheItem,
        IReadOnlyList<string> coveRoots,
        string name,
        CancellationToken ct)
    {
        var held = await heldByTheItem(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(held)
            || TreePathGuard.IsInsideATree(held, coveRoots)
            || FolderOf(held) is not { } folder
                ? null
                : PathCandidateGuard.CandidateUnder(folder, name);
    }

    private static bool HoldsTheSameFile(ITreeLinkPort links, string arrivalPath, string destination)
        => links.Identify(arrivalPath) is { } arrival
            && links.Identify(destination) is { } there
            && arrival == there;

    // Null for a path with no folder above it to speak of, which is the root itself.
    private static string? FolderOf(string path)
    {
        var normalized = PathCandidateGuard.Normalize(path);
        var cut = normalized.LastIndexOf('/');
        return cut <= 0 ? null : normalized[..cut];
    }
}
