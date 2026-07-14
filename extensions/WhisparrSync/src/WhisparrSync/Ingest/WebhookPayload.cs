namespace WhisparrSync.Ingest;

/// <summary>
/// The defensively-parsed Whisparr On-Import webhook body. Every field is nullable and unknown
/// properties are ignored (the real <c>Download</c> body carries far more than the receiver reads), so a
/// schema drift degrades to a no-op rather than a throw. Only <see cref="EventType"/> plus, for a
/// Download, <see cref="MovieFile"/>.<c>Path</c> / <see cref="IsUpgrade"/> / <see cref="DownloadId"/> are
/// load-bearing here (03-RESEARCH.md Pitfall 4).
/// </summary>
internal sealed record WebhookPayload
{
    public string? EventType { get; init; }
    public bool IsUpgrade { get; init; }
    public string? DownloadId { get; init; }
    public WebhookMovie? Movie { get; init; }
    public WebhookMovieFile? MovieFile { get; init; }
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
