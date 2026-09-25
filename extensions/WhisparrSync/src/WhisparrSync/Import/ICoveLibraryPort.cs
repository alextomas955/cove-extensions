using WhisparrSync.Identity;

namespace WhisparrSync.Import;

/// <summary>How one call to the host's own import ended.</summary>
/// <remarks>
/// An import the container could not produce and a file the host refused are separate states. Only
/// one of the two is something a user can act on.
/// </remarks>
public enum LibraryImportOutcome
{
    /// <summary>The host took the file and the library holds it.</summary>
    Registered,

    /// <summary>The host's import could not be obtained from this extension's container.</summary>
    ServiceUnavailable,

    /// <summary>The host was asked to take a verified file and would not.</summary>
    HostRefused,
}

/// <summary>What one call to the host's own import produced.</summary>
/// <remarks>The video key is null when the call registered nothing.</remarks>
public sealed record LibraryImport(LibraryImportOutcome Outcome, int? VideoId);

/// <summary>A file row the library holds at one path.</summary>
/// <remarks>
/// A null video key is a row no item claims, the state
/// <see cref="ICoveLibraryPort.DetachSupersededFilesAsync"/> leaves behind. Reading it as "the
/// library holds nothing here" would hand the host's own import a path it already has a row for and
/// no item to attach it to, which is the one input that import answers by throwing.
/// </remarks>
public sealed record HeldFile(int? VideoId);

/// <summary>Which video a remote identifier names, or why none can be named.</summary>
/// <remarks>
/// Exactly one of the three states is reachable, and nothing outside <see cref="ICoveLibraryPort"/>
/// can construct a reading in which more than one is set.
/// </remarks>
public sealed record IdentityResolution
{
    private IdentityResolution(int? videoId, bool ambiguous)
    {
        VideoId = videoId;
        Ambiguous = ambiguous;
    }

    /// <summary>The one video carrying the identifier, or null when there is not exactly one.</summary>
    public int? VideoId { get; }

    /// <summary>Whether more than one video carries the identifier.</summary>
    public bool Ambiguous { get; }

    /// <summary>No video carries the identifier. A normal state, not an error.</summary>
    public static IdentityResolution Unmatched { get; } = new(null, false);

    /// <summary>More than one video carries it, so no video can be named.</summary>
    public static IdentityResolution TooMany { get; } = new(null, true);

    internal static IdentityResolution At(int videoId) => new(videoId, false);
}

/// <summary>The record a metadata source supplied could not be written to the library.</summary>
/// <remarks>
/// Raised apart from a source that applied nothing, because a caller that cannot tell the two apart
/// can only report the source, which in this case answered.
/// </remarks>
public sealed class EnrichmentNotCommittedException(Exception inner)
    : Exception("The record the metadata source supplied could not be committed.", inner);

/// <summary>
/// The one seam through which this extension reaches Cove's own library.
/// </summary>
/// <remarks>
/// Registering a file is the host's own operation, never rows written here: it also probes the
/// media, computes a hash, discovers caption sidecars, creates the folder row under a striped lock,
/// recomputes the item's aggregates and publishes the event a client listens for.
/// <para>
/// Every read is taken live against the host and no match is cached, so no state of this
/// extension's can disagree with the library.
/// </para>
/// </remarks>
public interface ICoveLibraryPort
{
    /// <summary>The host's configured library paths, blank entries dropped.</summary>
    IReadOnlyList<string> LibraryRoots { get; }

    /// <summary>The endpoints of the metadata sources the host is configured with.</summary>
    /// <remarks>
    /// The host's own merge writes an identity row under the configured spelling and dedupes those
    /// rows by exact string, while resolving an endpoint to a source on the registrable domain. A
    /// stamp written under a different spelling of the same source acquires a second row on the
    /// next merge, with nothing at the database level to prevent it.
    /// </remarks>
    IReadOnlyList<string> ConfiguredMetadataEndpoints { get; }

    /// <summary>Registers the file at <paramref name="path"/> as a video.</summary>
    /// <remarks>
    /// <paramref name="path"/> must be one <see cref="PathCandidateGuard"/> constructed and a probe
    /// verified. The host's own import resolves a folder row from whatever directory it is handed
    /// and consults no library root, so passing a reported string here would register a file from
    /// outside the library and create an orphan folder tree for it.
    /// <para>
    /// A null <paramref name="videoId"/> lets the host decide. Supplying one on an item whose title
    /// is blank also makes the host fill that title from the file name.
    /// </para>
    /// </remarks>
    Task<LibraryImport> ImportVideoAsync(string path, int? videoId, CancellationToken ct);

    /// <summary>The file row the library holds at <paramref name="path"/>.</summary>
    /// <returns>
    /// Null when the library holds no row there. A row is answered as itself whether or not an item
    /// claims it, because those are different states and the caller has to act differently on each.
    /// </returns>
    Task<HeldFile?> HeldFileAtAsync(string path, CancellationToken ct);

    /// <summary>
    /// Clears the video key on every file row of <paramref name="videoId"/> except the row at
    /// <paramref name="keptPath"/>.
    /// </summary>
    /// <remarks>
    /// The row's video key and nothing else. No file is moved, renamed or deleted in either
    /// system's storage, and this port declares no member that could.
    /// <para>
    /// The rows are one item's, so this is bounded by that item's file count, never by the library.
    /// </para>
    /// <para>
    /// The item's file count, duration, resolution and path figures are recomputed by the host's
    /// save, which gathers both the current and the original value of a changed video key.
    /// </para>
    /// </remarks>
    /// <returns>How many rows were detached.</returns>
    Task<int> DetachSupersededFilesAsync(int videoId, string keptPath, CancellationToken ct);

    /// <summary>Starts one host scan over <paramref name="paths"/> and returns without waiting.</summary>
    /// <remarks>
    /// The host's enqueue deduplicates nothing and defaults to exclusive, so one call per imported
    /// file would serialise a burst of grabs into a burst of library scans. A caller passes the
    /// verified absolute paths of a batch.
    /// <para>
    /// Otherwise-unchanged discovered files are included in the asset-generation pass: the files
    /// were registered before the scan job starts, so a pass that skipped them would find nothing
    /// to do.
    /// </para>
    /// </remarks>
    /// <returns>Whether the host's scan service could be reached at all.</returns>
    bool StartFollowUpScan(IReadOnlyList<string> paths);

    /// <summary>Which video carries <paramref name="remoteId"/> for the source at <paramref name="endpoint"/>.</summary>
    /// <remarks>
    /// Endpoints are compared through <see cref="EndpointMatchGuard"/> rather than as strings, so a
    /// row stored under another spelling of the same source is found. Two videos carrying it is
    /// answered as such and never as the first of them.
    /// </remarks>
    Task<IdentityResolution> ResolveByRemoteIdAsync(string endpoint, string remoteId, CancellationToken ct);

    Task<bool> CarriesIdentityAsync(int videoId, string endpoint, CancellationToken ct);

    /// <summary>Writes one identity row for <paramref name="videoId"/>, if it carries none yet.</summary>
    /// <remarks>
    /// There is no unique constraint on the video-and-endpoint pair, so the read this performs first
    /// is the only thing standing between one source and two rows.
    /// </remarks>
    /// <returns>Whether a row was written.</returns>
    Task<bool> StampIdentityAsync(int videoId, string endpoint, string remoteId, CancellationToken ct);

    /// <summary>Applies the source's record for <paramref name="remoteId"/> to <paramref name="videoId"/>.</summary>
    /// <remarks>
    /// The host applies its record with the overwriting default when no import configuration is
    /// passed, and no configuration at that call would make a second application safe. A caller
    /// must never make this call twice for one scene.
    /// </remarks>
    /// <returns>Whether the host could be reached and applied a record.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="endpoint"/> matches no metadata source the host is configured with. A caller
    /// that cannot guarantee one must treat this as best-effort and catch it.
    /// </exception>
    /// <exception cref="EnrichmentNotCommittedException">
    /// The source applied its record and it could not be written.
    /// </exception>
    Task<bool> EnrichAsync(int videoId, string endpoint, string remoteId, CancellationToken ct);
}
