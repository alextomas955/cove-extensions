using WhisparrSync.Addressing;
using WhisparrSync.Contracts;

namespace WhisparrSync.Monitoring;

/// <summary>Which root one entity is registered at, and what was left behind by choosing it.</summary>
/// <param name="InstanceRoot">
/// The instance root to register at, or null where none was chosen or none was agreed.
/// </param>
/// <param name="Refusal">
/// Why no instance root was chosen, or <see cref="MonitorRefusalKind.None"/>. An entity owning no
/// file is <see cref="MonitorRefusalKind.None"/> with a null root: nothing was refused, there was
/// simply nothing to derive a root from.
/// </param>
/// <param name="CoveRoot">The library root the choice was made from, or null where none was.</param>
/// <param name="FilesAtChosenRoot">How many of the entity's files sit under the chosen root.</param>
/// <param name="FilesLeftElsewhere">How many sit under a root that was not chosen.</param>
/// <param name="RootsLeftBehind">
/// The library roots holding some of the entity's files that were not chosen.
/// </param>
internal sealed record EntityRoot(
    string? InstanceRoot,
    MonitorRefusalKind Refusal,
    string? CoveRoot,
    int FilesAtChosenRoot,
    int FilesLeftElsewhere,
    IReadOnlyList<string> RootsLeftBehind);

/// <summary>Chooses the library root an entity's own files sit under, and resolves it.</summary>
/// <remarks>
/// Pure. It drives the two delegates it is given and performs no I/O of its own.
/// <para>
/// Cove stores no path on a studio, so there is no value to read: the root is counted from the files
/// and only then resolved to an instance root.
/// </para>
/// </remarks>
internal static class EntityRootStep
{
    /// <summary>
    /// The instance root to register an entity at, chosen from <paramref name="coveRoots"/>.
    /// </summary>
    /// <remarks>
    /// The root holding most of the entity's files, with no threshold and no second rule: the same
    /// rule applies whether the split is 883 to 2 or 45 to 40. An exact tie takes the root the
    /// supplied list names first, which is the host's own configured order, so two runs over one
    /// configuration choose the same root.
    /// <para>
    /// Only the chosen root is asked about, so a run over hundreds of entities still establishes one
    /// agreement per root rather than one per entity.
    /// </para>
    /// <para>
    /// Nothing is imported into the chosen root and no file is moved. The files are the library's,
    /// and this product relocating them is a change the owner cannot undo; the roots holding the
    /// strays are reported instead.
    /// </para>
    /// <para>
    /// <see cref="EntityRoot.RootsLeftBehind"/> is bounded by the configured root count, which an
    /// operator creates by hand. It carries root names only, never an entity, a folder or a file.
    /// </para>
    /// </remarks>
    /// <param name="coveRoots">The configured library roots, in the host's own configured order.</param>
    /// <param name="countUnder">How many of the entity's files sit under one library root.</param>
    /// <param name="agreedRoot">What instance root one library root agrees with.</param>
    /// <param name="ct">Cancels the reads.</param>
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
