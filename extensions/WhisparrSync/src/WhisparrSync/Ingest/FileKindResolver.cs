namespace WhisparrSync.Ingest;

/// <summary>The Cove media kind an imported file resolves to — one per <c>IScanService.ImportDownloaded*</c> method.</summary>
internal enum IngestKind
{
    Video,
    Image,
    Gallery,
    Audio,
    Text,
}

/// <summary>
/// Resolves an imported file path to its <see cref="IngestKind"/> by file extension (case-insensitive),
/// mirroring Renamer's <c>TryParseKind</c> shape: a <c>bool</c> + <c>out</c> enum, never <c>Enum.Parse</c>.
/// </summary>
/// <remarks>
/// The extension lists default to the Cove.Core <c>CoveConfiguration</c> defaults (Configuration.cs:34-38).
/// Reading the user's configured lists from the overlay scope is deferred to 03-03 (RESEARCH Open Q1); a
/// user who added a non-default extension simply routes to the manual-scan fallback (IMPT-05) until then.
/// </remarks>
internal static class FileKindResolver
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".m4v", ".mp4", ".mov", ".wmv", ".avi", ".mpg", ".mpeg", ".rmvb", ".rm", ".flv", ".asf", ".mkv", ".webm", ".f4v" };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif" };

    private static readonly HashSet<string> GalleryExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".zip", ".cbz" };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".m4a", ".m4b", ".flac", ".wav", ".ogg", ".oga", ".opus", ".aac", ".alac", ".aif", ".aiff", ".wma", ".mka", ".weba", ".amr" };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".txt", ".md", ".markdown", ".pdf", ".epub", ".rtf", ".nfo", ".log", ".srt", ".vtt", ".ass", ".ssa", ".lrc", ".html", ".htm" };

    /// <summary>Resolves <paramref name="path"/>'s extension to a kind; false (kind defaulted) for an empty/extension-less/unknown path.</summary>
    public static bool TryResolve(string? path, out IngestKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            return false;
        }

        if (VideoExtensions.Contains(ext))
        {
            kind = IngestKind.Video;
            return true;
        }

        if (ImageExtensions.Contains(ext))
        {
            kind = IngestKind.Image;
            return true;
        }

        if (GalleryExtensions.Contains(ext))
        {
            kind = IngestKind.Gallery;
            return true;
        }

        if (AudioExtensions.Contains(ext))
        {
            kind = IngestKind.Audio;
            return true;
        }

        if (TextExtensions.Contains(ext))
        {
            kind = IngestKind.Text;
            return true;
        }

        return false;
    }
}
