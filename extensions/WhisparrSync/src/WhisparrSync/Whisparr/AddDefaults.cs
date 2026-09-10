namespace WhisparrSync.Whisparr;

/// <summary>The instance-side values an add cannot be composed without.</summary>
/// <remarks>
/// Read from the instance at action time and handed to the acting member, so no acting member reads
/// anything for itself and there is no member a caller could aim at a value the instance never
/// offered.
/// </remarks>
public sealed record AddDefaults(int QualityProfileId, string RootFolderPath);
