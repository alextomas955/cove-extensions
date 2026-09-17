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

        string? chosen = null;
        var atChosen = 0;
        foreach (var root in coveRoots)
        {
            var count = await countUnder(root, ct).ConfigureAwait(false);
            if (count > atChosen)
            {
                chosen = root;
                atChosen = count;
            }
        }

        if (chosen is null)
        {
            return new EntityRoot(null, MonitorRefusalKind.None, null, 0, 0, []);
        }

        var addressed = await agreedRoot(chosen, ct).ConfigureAwait(false);

        return addressed.InstancePath is { } agreed
            ? new EntityRoot(agreed, MonitorRefusalKind.None, chosen, atChosen, 0, [])
            : new EntityRoot(
                null, MonitorRefusalKind.NoAgreedRootForThisEntity, chosen, atChosen, 0, []);
    }
}
