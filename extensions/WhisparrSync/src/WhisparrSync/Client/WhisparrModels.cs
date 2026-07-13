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

/// <summary>
/// A Whisparr v3 <c>GET /api/v3/movie</c> row — the MATCH-01 reconciliation data source. Field set
/// transcribed from the devopsarr/whisparr-go generated <c>MovieResource</c> model and confirmed against
/// the live v3.3.4.794 instance; only the fields identity matching needs are modeled (nullable-friendly
/// so a partial row still deserializes). The full set is returned unpaged (issue #218), so the
/// <see cref="StashId"/> index is built client-side rather than via a server-side query.
/// </summary>
/// <remarks>
/// Field polymorphism (why the matcher must not treat these uniformly): <see cref="StashId"/> is the
/// scene's StashDB UUID when present; <see cref="ForeignId"/> is a tmdbId when <see cref="ItemType"/>
/// is <c>"movie"</c> and the StashDB UUID when <see cref="ItemType"/> is <c>"scene"</c> — so a Cove
/// StashDB id must be matched against <see cref="StashId"/> first, and against <see cref="ForeignId"/>
/// only for scene rows.
/// </remarks>
internal sealed record WhisparrMovie(
    int Id,
    string? Title,
    int? Year,
    string? StashId,
    string? ForeignId,
    string? ItemType,
    bool Monitored,
    bool HasFile,
    WhisparrMovieFile? MovieFile);

/// <summary>
/// The on-disk file of a <see cref="WhisparrMovie"/> (the <c>movieFile</c> sub-resource). <see cref="Path"/>
/// is the source of the reconciliation path leg; nullable because a not-yet-downloaded movie has none.
/// </summary>
internal sealed record WhisparrMovieFile(int Id, string? Path);
