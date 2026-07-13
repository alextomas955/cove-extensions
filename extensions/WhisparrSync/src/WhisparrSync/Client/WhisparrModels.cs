namespace WhisparrSync.Client;

/// <summary>
/// The Whisparr v3 <c>GET /api/v3/system/status</c> projection. Field set transcribed from the
/// devopsarr/whisparr-go generated <c>SystemResource</c> model; only the fields this phase needs are
/// modeled. <see cref="Version"/> is the dotted app version (e.g. <c>3.3.4.808</c>) whose major segment
/// the adapter gate reads; <see cref="InstanceName"/> is shown on a successful Test connection.
/// </summary>
internal sealed record SystemStatus(string? Version, string? AppName, string? InstanceName, string? Branch);

/// <summary>
/// A Whisparr v3 <c>GET /api/v3/rootfolder</c> row. Field set transcribed from the devopsarr/whisparr-go
/// generated <c>RootFolderResource</c> model; only the fields the settings UI needs are modeled.
/// <see cref="Accessible"/> is Whisparr's own reachability flag and <see cref="FreeSpace"/> is bytes free
/// (both nullable-friendly so a partial row still deserializes).
/// </summary>
internal sealed record RootFolder(int Id, string? Path, bool Accessible, long? FreeSpace);

/// <summary>
/// A Whisparr v3 <c>GET /api/v3/qualityprofile</c> row. Field set transcribed from the
/// devopsarr/whisparr-go generated <c>QualityProfileResource</c> model; the settings UI needs only the
/// stable <see cref="Id"/> (the persisted selection) and the display <see cref="Name"/>.
/// </summary>
internal sealed record QualityProfile(int Id, string? Name);
