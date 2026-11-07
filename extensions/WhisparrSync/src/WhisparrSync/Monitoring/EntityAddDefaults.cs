using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Linking;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Monitoring;

internal sealed record EntityAddDefaultsResolution(
    AddDefaults? Defaults, MonitorRefusalKind Refusal, EntityRoot Root);

// The one place an add body's root is composed, and the one place its folder is. The quality
// profile stays the run-wide value, but the root does not: registering an entity where none of its
// files sit leaves the instance holding an entry that can never link anything. An entity owning no
// file keeps the run-wide root, since it has nothing to derive one from.
internal static class EntityAddDefaults
{
    // folderFor is the identifier of an entity this product builds a folder for, and null for one
    // it does not. Null is not a gap: a folder is expressible only where the add schema declares a
    // path member, and a member a schema does not declare is discarded by the instance without
    // saying so, which would leave this product reading a folder it never registered.
    internal static async Task<EntityAddDefaultsResolution> ComposeAsync(
        AddDefaults runWide,
        IReadOnlyList<string> coveRoots,
        WhisparrGeneration generation,
        string? folderFor,
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
                runWide with
                {
                    RootFolderPath = agreed,
                    EntityFolderPath = FolderUnder(agreed, generation, folderFor),
                },
                MonitorRefusalKind.None,
                root);
        }

        return root.Refusal is MonitorRefusalKind.None
            ? new EntityAddDefaultsResolution(runWide, MonitorRefusalKind.None, root)
            : new EntityAddDefaultsResolution(null, root.Refusal, root);
    }

    // Composed on the instance's own spelling of the root rather than on the library's, through the
    // same two joins the folder on disk is built with, so the two cannot disagree about where the
    // entity's links are. No probe: the root agreed already, and everything below it is arithmetic.
    private static string? FolderUnder(
        string instanceRoot, WhisparrGeneration generation, string? folderFor)
        => string.IsNullOrWhiteSpace(folderFor)
            || TreePathGuard.TreeRootUnder(instanceRoot, generation) is not { } tree
                ? null
                : TreePathGuard.EntityFolderIn(tree, folderFor);
}
