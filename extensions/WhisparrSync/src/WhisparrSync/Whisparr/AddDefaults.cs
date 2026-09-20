namespace WhisparrSync.Whisparr;

/// <summary>The instance-side values an add cannot be composed without.</summary>
/// <remarks>Read from the instance at action time, so no acting member reads one for itself.</remarks>
public sealed record AddDefaults(int QualityProfileId, string RootFolderPath);
