using System.Globalization;

namespace WhisparrSync.Whisparr;

// Where an entity's registration is moved to, as both generations spell it.
//
// The instance derives the root from the path, so the path is what relocates an entity and a root
// folder changed on its own is accepted and relocates nothing. The root is sent beside it to state
// the intent.
//
// A folder this product built is sent as it is rather than recomposed: its last segment names the
// entity, and the entity's own held path names wherever the instance put it, so carrying that
// segment across would move the entity to a folder no entity owns. With no folder built, the held
// path's last segment is carried under the new root, which is where an entity registered before
// this product built folders sits.
internal static class MovedEntityFolder
{
    internal static (string Path, string Root) Composed(
        string heldPath, string instanceRoot, string? entityFolderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heldPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceRoot);

        var separator = SeparatorOf(heldPath);
        var root = Respelled(instanceRoot, separator);

        return (
            entityFolderPath is { } folder ? Respelled(folder, separator) : Under(root, heldPath, separator),
            root);
    }

    private static string Under(string root, string path, char separator)
    {
        var trimmedPath = path.TrimEnd('/', '\\');
        var lastSegment = trimmedPath[(trimmedPath.LastIndexOfAny(['/', '\\']) + 1)..];

        return lastSegment.Length == 0
            ? root
            : string.Create(CultureInfo.InvariantCulture, $"{root}{separator}{lastSegment}");
    }

    // The separator comes off the entity's own held path rather than off the root. The root arrives
    // from the addressing port, which spells every candidate with forward slashes whichever host the
    // instance runs on, and joining a Windows path with a forward slash yields one that instance
    // will not resolve.
    private static char SeparatorOf(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        return trimmed.Contains('\\', StringComparison.Ordinal)
            && !trimmed.Contains('/', StringComparison.Ordinal)
                ? '\\'
                : '/';
    }

    private static string Respelled(string root, char separator)
        => root.TrimEnd('/', '\\').Replace(separator == '\\' ? '/' : '\\', separator);
}
