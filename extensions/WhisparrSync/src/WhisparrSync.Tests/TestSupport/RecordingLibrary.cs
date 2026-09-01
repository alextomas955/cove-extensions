using WhisparrSync.Import;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>The host library, faked, recording the ARGUMENTS it was called with.</summary>
/// <remarks>
/// Arguments and not counts: a fake that recorded only how often it was called would pass a test of
/// "the file the extension verified reaches the host" whatever path was handed over. The one count
/// kept is the enrichment call's, because "at most once per scene" is a claim about the count.
/// </remarks>
/// <param name="reached">
/// Whether this extension's container could produce the host's import service at all. False is the
/// host's own configuration, which is a different answer from a file the host declined.
/// </param>
/// <param name="roots">The host's configured library paths.</param>
internal sealed class RecordingLibrary(bool reached, IReadOnlyList<string> roots) : ICoveLibraryPort
{
    /// <summary>The paths handed to the host's import, with the item each was attached to.</summary>
    public List<(string Path, int? VideoId)> Imported { get; } = [];

    /// <summary>The identity rows written, newest last.</summary>
    public List<(int VideoId, string Endpoint, string RemoteId)> Stamped { get; } = [];

    /// <summary>Every enrichment call, in order.</summary>
    public List<(int VideoId, string Endpoint, string RemoteId)> Enriched { get; } = [];

    /// <summary>The identity rows the library already holds before any delivery.</summary>
    public List<(int VideoId, string Endpoint, string RemoteId)> ExistingIdentities { get; } = [];

    /// <summary>The file rows the library already holds, keyed by path.</summary>
    public Dictionary<string, HeldFile> Held { get; } = [];

    /// <summary>Every path the live dedupe read asked about, in order.</summary>
    /// <remarks>
    /// The count is the claim here: the answer is derived per delivery, so a delivery that skipped
    /// the read would be working from something remembered.
    /// </remarks>
    public List<string> Probed { get; } = [];

    /// <summary>The metadata sources the host is configured with.</summary>
    public List<string> ConfiguredEndpoints { get; } = [];

    /// <summary>Every follow-up scan started, with the paths each was asked to cover.</summary>
    public List<IReadOnlyList<string>> Scans { get; } = [];

    /// <summary>Whether the host's scan service can be reached for a follow-up.</summary>
    public bool FollowUpScanIsReachable { get; set; } = true;

    /// <summary>Raised by <see cref="EnrichAsync"/> instead of returning, when set.</summary>
    public Exception? EnrichmentFailure { get; set; }

    /// <summary>Whether a second video carries whatever identifier is resolved.</summary>
    public bool IdentityIsAmbiguous { get; set; }

    /// <summary>The item the host's import attaches a new file to.</summary>
    public int ImportedVideoId { get; set; } = 1;

    public IReadOnlyList<string> LibraryRoots => roots;

    public IReadOnlyList<string> ConfiguredMetadataEndpoints => ConfiguredEndpoints;

    /// <summary>The exception the host's own import raises, which the real port contains.</summary>
    /// <remarks>
    /// Answered here the way the port answers it, never raised: the containment lives in the port,
    /// and a fake that raised would be standing in for the host rather than for the seam. What the
    /// real port does with each of these is proven against a real port and a raising scan service.
    /// </remarks>
    public Exception? ImportFailure { get; set; }

    public Task<LibraryImport> ImportVideoAsync(string path, int? videoId, CancellationToken ct)
    {
        Imported.Add((path, videoId));
        if (!reached)
        {
            return Task.FromResult(new LibraryImport(LibraryImportOutcome.ServiceUnavailable, null));
        }

        if (ImportFailure is not null)
        {
            return Task.FromResult(new LibraryImport(LibraryImportOutcome.HostRefused, null));
        }

        return Task.FromResult(
            new LibraryImport(LibraryImportOutcome.Registered, videoId ?? ImportedVideoId));
    }

    /// <summary>Every detach asked for, with the path each one kept.</summary>
    public List<(int VideoId, string KeptPath)> Detached { get; } = [];

    /// <summary>How many rows a detach reports having cleared.</summary>
    public int DetachedRowCount { get; set; } = 1;

    public Task<int> DetachSupersededFilesAsync(int videoId, string keptPath, CancellationToken ct)
    {
        Detached.Add((videoId, keptPath));
        return Task.FromResult(DetachedRowCount);
    }

    public bool StartFollowUpScan(IReadOnlyList<string> paths)
    {
        if (!FollowUpScanIsReachable)
        {
            return false;
        }

        Scans.Add(paths);
        return true;
    }

    public Task<HeldFile?> HeldFileAtAsync(string path, CancellationToken ct)
    {
        Probed.Add(path);
        return Task.FromResult(Held.GetValueOrDefault(path));
    }

    public Task<IdentityResolution> ResolveByRemoteIdAsync(
        string endpoint, string remoteId, CancellationToken ct)
    {
        if (IdentityIsAmbiguous)
        {
            return Task.FromResult(IdentityResolution.TooMany);
        }

        var carriers = Rows()
            .Where(row => row.RemoteId == remoteId
                && EndpointMatchGuard.SameSource(row.Endpoint, endpoint))
            .Select(row => row.VideoId)
            .Distinct()
            .ToList();

        return Task.FromResult(carriers.Count switch
        {
            0 => IdentityResolution.Unmatched,
            1 => IdentityResolution.At(carriers[0]),
            _ => IdentityResolution.TooMany,
        });
    }

    public Task<bool> CarriesIdentityAsync(int videoId, string endpoint, CancellationToken ct)
        => Task.FromResult(Rows().Any(row => row.VideoId == videoId
            && EndpointMatchGuard.SameSource(row.Endpoint, endpoint)));

    public async Task<bool> StampIdentityAsync(
        int videoId, string endpoint, string remoteId, CancellationToken ct)
    {
        if (await CarriesIdentityAsync(videoId, endpoint, ct))
        {
            return false;
        }

        Stamped.Add((videoId, endpoint, remoteId));
        return true;
    }

    public Task<bool> EnrichAsync(int videoId, string endpoint, string remoteId, CancellationToken ct)
    {
        Enriched.Add((videoId, endpoint, remoteId));
        return EnrichmentFailure is { } failure ? Task.FromException<bool>(failure) : Task.FromResult(true);
    }

    private IEnumerable<(int VideoId, string Endpoint, string RemoteId)> Rows()
        => ExistingIdentities.Concat(Stamped);
}
