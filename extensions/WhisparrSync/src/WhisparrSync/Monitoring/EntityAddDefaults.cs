using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Monitoring;

/// <summary>What one entity is added with, or why no add can be composed for it.</summary>
/// <param name="Defaults">The values to compose with, or null on a refusal.</param>
/// <param name="Refusal">Why there are none, or <see cref="MonitorRefusalKind.None"/>.</param>
/// <param name="Root">What the root choice found, whether or not it yielded one.</param>
internal sealed record EntityAddDefaultsResolution(
    AddDefaults? Defaults, MonitorRefusalKind Refusal, EntityRoot Root);

/// <summary>
/// The one seam an add body's root comes from, so no call site composes its own.
/// </summary>
/// <remarks>
/// Pure. The quality profile stays the run-wide value read from the instance, because one profile is
/// as good as the instance's own first answer and nothing about an entity makes another right. The
/// root is not that kind of value: registering a studio where none of its files sit leaves the
/// instance holding an entry that can never link anything.
/// <para>
/// An entity owning no file keeps the run-wide root. It has nothing to derive one from, and refusing
/// it would stop adds that work today for a defect they do not have.
/// </para>
/// </remarks>
internal static class EntityAddDefaults
{
    /// <summary>
    /// <paramref name="runWide"/> with the root the entity's own files sit under.
    /// </summary>
    /// <param name="runWide">The values read once from the instance for this run.</param>
    /// <param name="coveRoots">The configured library roots, in the host's own configured order.</param>
    /// <param name="countUnder">How many of the entity's files sit under one library root.</param>
    /// <param name="agreedRoot">What instance root one library root agrees with.</param>
    /// <param name="ct">Cancels the reads.</param>
    internal static async Task<EntityAddDefaultsResolution> ComposeAsync(
        AddDefaults runWide,
        IReadOnlyList<string> coveRoots,
        Func<string, CancellationToken, Task<int>> countUnder,
        Func<string, CancellationToken, Task<AddressedFolder>> agreedRoot,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runWide);

        var root = await EntityRootStep
            .ResolveAsync(coveRoots, countUnder, agreedRoot, ct).ConfigureAwait(false);

        if (root.InstanceRoot is { } agreed)
        {
            return new EntityAddDefaultsResolution(
                runWide with { RootFolderPath = agreed }, MonitorRefusalKind.None, root);
        }

        return root.Refusal is MonitorRefusalKind.None
            ? new EntityAddDefaultsResolution(runWide, MonitorRefusalKind.None, root)
            : new EntityAddDefaultsResolution(null, root.Refusal, root);
    }
}
