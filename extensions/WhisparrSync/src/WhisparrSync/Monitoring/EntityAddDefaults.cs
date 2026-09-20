using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Monitoring;

internal sealed record EntityAddDefaultsResolution(
    AddDefaults? Defaults, MonitorRefusalKind Refusal, EntityRoot Root);

// The one place an add body's root is composed. The quality profile stays the run-wide value, but
// the root does not: registering an entity where none of its files sit leaves the instance holding
// an entry that can never link anything. An entity owning no file keeps the run-wide root, since it
// has nothing to derive one from.
internal static class EntityAddDefaults
{
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
