using WhisparrSync.Import;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>The host library, faked, recording the ARGUMENTS it was called with.</summary>
/// <remarks>
/// Arguments and not counts: a fake that recorded only how often it was called would pass a test of
/// "the file the extension verified reaches the host" whatever path was handed over. The one count
/// kept is the enrichment call's, because "at most once per scene" is a claim about the count.
/// </remarks>
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

    /// <summary>The video the library already holds a file for, keyed by path.</summary>
    public Dictionary<string, int?> Held { get; } = [];

    /// <summary>The metadata sources the host is configured with.</summary>
    public List<string> ConfiguredEndpoints { get; } = [];

    /// <summary>Raised by <see cref="EnrichAsync"/> instead of returning, when set.</summary>
    public Exception? EnrichmentFailure { get; set; }

    /// <summary>Whether a second video carries whatever identifier is resolved.</summary>
    public bool IdentityIsAmbiguous { get; set; }

    /// <summary>The item the host's import attaches a new file to.</summary>
    public int ImportedVideoId { get; set; } = 1;

    public IReadOnlyList<string> LibraryRoots => roots;

    public IReadOnlyList<string> ConfiguredMetadataEndpoints => ConfiguredEndpoints;

    public Task<LibraryImport> ImportVideoAsync(string path, int? videoId, CancellationToken ct)
    {
        Imported.Add((path, videoId));
        return Task.FromResult(new LibraryImport(reached, reached ? videoId ?? ImportedVideoId : null));
    }

    public Task<int?> VideoHoldingFileAtAsync(string path, CancellationToken ct)
        => Task.FromResult(Held.TryGetValue(path, out var videoId) ? videoId : null);

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
