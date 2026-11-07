using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Linking;

/// <summary>What one entity's pass did about where the instance records it.</summary>
internal enum RelocationAct
{
    /// <summary>The instance already records the entity at the folder this run intends.</summary>
    AlreadyThere,

    /// <summary>
    /// Nothing said where the instance records the entity, or nothing agreed where it belongs, so
    /// nothing was sent.
    /// </summary>
    NothingToCompare,

    /// <summary>The instance accepted the new folder.</summary>
    Moved,

    /// <summary>
    /// The instance was asked and would not, or no relocation role was obtained at all. Either way
    /// the entity is still registered where none of its files sit.
    /// </summary>
    Declined,
}

internal sealed record TreeRelocation(RelocationAct Act, WhisparrResponse? Answer)
{
    internal static TreeRelocation NothingToCompare { get; } =
        new(RelocationAct.NothingToCompare, null);

    internal static TreeRelocation AlreadyThere { get; } = new(RelocationAct.AlreadyThere, null);
}

/// <summary>
/// Moves where the instance records one entity, to the folder this run built for it.
/// </summary>
/// <remarks>
/// The order is the whole of this: the folder and its links exist before the move is sent, and the
/// name left on the old drive is taken back afterwards by the pass that sweeps the tree. Both
/// generations rewrite their own file records to the new folder without reading it, so a move sent
/// before the links exist leaves the instance reporting a file at a path nothing holds, measured on
/// each of them across two drives.
/// <para>
/// This issues no removal. A name whose library file moved to another drive is one no library file
/// answers to any more, which is what the sweep decides from.
/// </para>
/// <para>
/// The folder is sent only where the caller built one. Where none was built the entity is moved onto
/// the root reaching its files, which is where an entity registered before this product built
/// folders sits.
/// </para>
/// </remarks>
internal static class TreeRelocationStep
{
    internal static async Task<TreeRelocation> RelocateAsync(
        string? registeredAt,
        EntityPlacement intended,
        Func<string, string?, CancellationToken, Task<WhisparrResponse?>>? relocate,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intended);

        if (registeredAt is not { } heldAt || intended.RootFolderPath is not { } root)
        {
            return TreeRelocation.NothingToCompare;
        }

        if (SamePlace(heldAt, intended.EntityFolderPath ?? root))
        {
            return TreeRelocation.AlreadyThere;
        }

        // A generation registering no relocation role refuses here rather than at the transport.
        if (relocate is null)
        {
            return new TreeRelocation(
                RelocationAct.Declined,
                new WhisparrResponse(0, null, string.Empty)
                {
                    Refusal = MonitorRefusalKind.CapabilityAbsentOnThisGeneration,
                });
        }

        var moved = await relocate(root, intended.EntityFolderPath, ct).ConfigureAwait(false);

        return moved is not null
            && MonitoringProjector.Accepted(moved) is MonitorRefusalKind.None
                ? new TreeRelocation(RelocationAct.Moved, moved)
                : new TreeRelocation(RelocationAct.Declined, moved);
    }

    // The intended path is built on the agreed root, which reaches here through the addressing
    // port, and that port spells every candidate with forward slashes and verifies it against the
    // instance's own listing without regard to case. The instance answers its own verbatim
    // spelling. Compared literally, a Windows instance holding D:\Media never matches the agreed
    // D:/Media, so every held entity is moved again on every run and the correction never converges.
    private static bool SamePlace(string registeredAt, string intended)
        => string.Equals(
            PathCandidateGuard.Normalize(registeredAt),
            PathCandidateGuard.Normalize(intended),
            StringComparison.OrdinalIgnoreCase);
}
