namespace WhisparrSync.Whisparr;

/// <summary>The instance-side values an add cannot be composed without.</summary>
/// <remarks>
/// Read from the instance at action time, so no acting member reads one for itself.
/// <para>
/// <c>EntityFolderPath</c> is the folder this extension built for the entity, as the instance
/// spells it, and is null for an entity that has none. The root stays beside it: the instance
/// derives the root from the path, and the root states the intent.
/// </para>
/// </remarks>
public sealed record AddDefaults(
    int QualityProfileId, string RootFolderPath, string? EntityFolderPath = null);
