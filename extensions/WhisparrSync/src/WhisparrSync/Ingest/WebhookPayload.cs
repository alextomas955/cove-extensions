namespace WhisparrSync.Ingest;

/// <summary>
/// The defensively-parsed Whisparr On-Import webhook body. Every field is nullable and unknown
/// properties are ignored (the real <c>Download</c> body carries far more than the receiver reads), so a
/// schema drift degrades to a no-op rather than a throw. Only <see cref="EventType"/> plus, for a
/// Download, <see cref="MovieFile"/>.<c>Path</c> (v3) — or, for Whisparr v2, <see cref="EpisodeFile"/>.<c>Path</c>
/// — / <see cref="IsUpgrade"/> / <see cref="DownloadId"/> are load-bearing here (03-RESEARCH.md Pitfall 4).
/// v2 posts <see cref="Series"/> + <see cref="Episodes"/> + <see cref="EpisodeFile"/> where v3 posts
/// <see cref="Movie"/> + <see cref="MovieFile"/>; the receiver reads whichever is present (version-blind).
/// </summary>
internal sealed record WebhookPayload
{
    public string? EventType { get; init; }
    public bool IsUpgrade { get; init; }
    public string? DownloadId { get; init; }
    public WebhookMovie? Movie { get; init; }
    public WebhookMovieFile? MovieFile { get; init; }

    // v2 (Sonarr-shaped) On-Import envelope: series + episodes[] + episodeFile in place of movie + movieFile.
    public WebhookSeries? Series { get; init; }
    public WebhookEpisode[]? Episodes { get; init; }
    public WebhookEpisodeFile? EpisodeFile { get; init; }
}

/// <summary>The imported scene's movie record; <see cref="Id"/> is the Whisparr movie id (the match handle, never a Cove id).</summary>
internal sealed record WebhookMovie
{
    public int Id { get; init; }
    public string? FolderPath { get; init; }
}

/// <summary>The imported file; <see cref="Path"/> is the final post-import/rename absolute path the coordinator ingests.</summary>
internal sealed record WebhookMovieFile
{
    public int Id { get; init; }
    public string? Path { get; init; }
    public string? RelativePath { get; init; }
}

/// <summary>The imported scene's v2 series record; <see cref="Id"/> is the Whisparr series id (v2 envelope only).</summary>
internal sealed record WebhookSeries
{
    public int Id { get; init; }
    public string? Path { get; init; }
}

/// <summary>A v2 imported episode; <see cref="Id"/> is the Whisparr episode id (the v2 match handle, never a Cove id).</summary>
internal sealed record WebhookEpisode
{
    public int Id { get; init; }
    public int? EpisodeFileId { get; init; }
}

/// <summary>The v2 imported file; <see cref="Path"/> is the final post-import/rename absolute path the coordinator ingests.</summary>
internal sealed record WebhookEpisodeFile
{
    public int Id { get; init; }
    public string? Path { get; init; }
    public string? RelativePath { get; init; }
}
