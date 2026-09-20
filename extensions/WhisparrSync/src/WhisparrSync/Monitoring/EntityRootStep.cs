using WhisparrSync.Addressing;
using WhisparrSync.Contracts;

namespace WhisparrSync.Monitoring;

// An entity owning no file carries a null InstanceRoot with Refusal None: nothing was refused,
// there was nothing to derive a root from.
internal sealed record EntityRoot(
    string? InstanceRoot,
    MonitorRefusalKind Refusal,
    string? CoveRoot,
    int FilesAtChosenRoot,
    int FilesLeftElsewhere,
    IReadOnlyList<string> RootsLeftBehind);

// Cove stores no path on a studio, so the root is counted from the entity's files and only then
// resolved to an instance root.
internal static class EntityRootStep
{
    // The root holding most of the entity's files, with no threshold. A tie takes the root the
    // supplied list names first, which is the host's configured order, so two runs over one
    // configuration choose the same root.
    //
    // No file is moved and nothing is imported into the chosen root; the roots holding the strays
    // are reported instead. RootsLeftBehind is bounded by the configured root count and carries
    // root names only.
    internal static async Task<EntityRoot> ResolveAsync(
        IReadOnlyList<string> coveRoots,
        Func<string, CancellationToken, Task<int>> countUnder,
        Func<string, CancellationToken, Task<AddressedFolder>> agreedRoot,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(coveRoots);
        ArgumentNullException.ThrowIfNull(countUnder);
        ArgumentNullException.ThrowIfNull(agreedRoot);

        var chosenAt = -1;
        var atChosen = 0;
        var counts = new int[coveRoots.Count];
        for (var index = 0; index < coveRoots.Count; index++)
        {
            counts[index] = await countUnder(coveRoots[index], ct).ConfigureAwait(false);

            // Strictly greater, so the first root of a tie keeps the choice.
            if (counts[index] > atChosen)
            {
                chosenAt = index;
                atChosen = counts[index];
            }
        }

        if (chosenAt < 0)
        {
            return new EntityRoot(null, MonitorRefusalKind.None, null, 0, 0, []);
        }

        var chosen = coveRoots[chosenAt];
        var leftBehind = coveRoots
            .Where((_, index) => index != chosenAt && counts[index] > 0)
            .ToList();
        var filesLeft = counts.Where((_, index) => index != chosenAt).Sum();

        var addressed = await agreedRoot(chosen, ct).ConfigureAwait(false);

        return addressed.InstancePath is { } agreed
            ? new EntityRoot(
                agreed, MonitorRefusalKind.None, chosen, atChosen, filesLeft, leftBehind)
            : new EntityRoot(
                null,
                MonitorRefusalKind.NoAgreedRootForThisEntity,
                chosen,
                atChosen,
                filesLeft,
                leftBehind);
    }
}
