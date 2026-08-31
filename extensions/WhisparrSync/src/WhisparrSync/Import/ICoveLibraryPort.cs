namespace WhisparrSync.Import;

/// <summary>What one call to the host's own import produced.</summary>
/// <param name="Reached">Whether the host's import service could be reached at all.</param>
/// <param name="VideoId">
/// The item the host attached the file to, or null when the call did not reach one.
/// </param>
public sealed record LibraryImport(bool Reached, int? VideoId);

/// <summary>
/// The one seam through which this extension reaches Cove's own library.
/// </summary>
/// <remarks>
/// Registering a file is the host's own operation, never rows written here: it also probes the
/// media, computes a hash, discovers caption sidecars, creates the folder row under a striped lock,
/// recomputes the item's aggregates and publishes the event a client listens for. A second
/// implementation of any of that would diverge on the next host release.
/// </remarks>
public interface ICoveLibraryPort
{
    /// <summary>The host's configured library paths, blank entries dropped.</summary>
    IReadOnlyList<string> LibraryRoots { get; }

    /// <summary>Registers the file at <paramref name="path"/> as a video.</summary>
    /// <remarks>
    /// <paramref name="path"/> must be one <see cref="PathCandidateGuard"/> constructed and a probe
    /// verified. The host's own import resolves a folder row from whatever directory it is handed
    /// and consults no library root, so passing a reported string here would register a file from
    /// outside the library and create an orphan folder tree for it.
    /// </remarks>
    /// <param name="path">The verified absolute path of the file to register.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<LibraryImport> ImportVideoAsync(string path, CancellationToken ct);
}
