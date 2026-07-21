namespace WhisparrSync.Ingest;

/// <summary>
/// Whether an imported path carries a Cove-recognized video extension (case-insensitive). Whisparr's own
/// On-Import webhook and history payloads only ever carry the scene's main media file
/// (<c>MovieFile</c>/<c>EpisodeFile</c>), so video is the only kind this extension's ingest path ever needs
/// to recognize — never an image, gallery, audio, or text path.
/// </summary>
/// <remarks>
/// The extension list defaults to the Cove.Core <c>CoveConfiguration</c> video-extension default
/// (Configuration.cs:34). Reading the user's configured list from the overlay scope is not yet wired; a
/// user who added a non-default video extension simply routes to the manual-scan fallback until then.
/// </remarks>
internal static class FileKindResolver
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".m4v", ".mp4", ".mov", ".wmv", ".avi", ".mpg", ".mpeg", ".rmvb", ".rm", ".flv", ".asf", ".mkv", ".webm", ".f4v" };

    /// <summary>True when <paramref name="path"/> has a recognized video extension; false for an empty/extension-less/unknown path.</summary>
    public static bool IsVideo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && VideoExtensions.Contains(ext);
    }
}
