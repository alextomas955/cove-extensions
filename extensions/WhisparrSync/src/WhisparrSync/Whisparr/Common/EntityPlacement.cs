namespace WhisparrSync.Whisparr;

/// <summary>Where one entity is to be registered on the instance.</summary>
/// <remarks>
/// Both members are the instance's own spelling. A null root leaves the run-wide one in place, and
/// a null folder leaves the instance to derive the path from the root, which is what an entity this
/// product built no folder for gets.
/// </remarks>
internal sealed record EntityPlacement(string? RootFolderPath, string? EntityFolderPath)
{
    /// <summary>Nothing settled, so the add carries the run-wide values.</summary>
    internal static EntityPlacement Nowhere { get; } = new(null, null);
}
