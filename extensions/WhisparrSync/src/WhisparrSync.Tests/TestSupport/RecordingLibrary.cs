using WhisparrSync.Identity;
using WhisparrSync.Import;

namespace WhisparrSync.Tests.TestSupport;

// Arguments and not counts: a fake recording only how often it was called would pass a test of "the
// file the extension verified reaches the host" whatever path was handed over. The one count kept
// is the enrichment call's, because "at most once per scene" is a claim about the count. A false
// reached is this extension's container not producing the host's import service at all, which
// differs from a file the host declined.
internal sealed class RecordingLibrary(bool reached, IReadOnlyList<string> roots) : ICoveLibraryPort
{
    public List<(string Path, int? VideoId)> Imported { get; } = [];

    public List<(int VideoId, string Endpoint, string RemoteId)> Stamped { get; } = [];

    public List<(int VideoId, string Endpoint, string RemoteId)> Enriched { get; } = [];

    public List<(int VideoId, string Endpoint, string RemoteId)> ExistingIdentities { get; } = [];

    public Dictionary<string, HeldFile> Held { get; } = [];

    // The count is the claim here: the answer is derived per delivery, so a delivery that skipped the
    // read would be working from something remembered.
    public List<string> Probed { get; } = [];

    public List<string> ConfiguredEndpoints { get; } = [];

    public List<IReadOnlyList<string>> Scans { get; } = [];

    public bool FollowUpScanIsReachable { get; set; } = true;

    public Exception? EnrichmentFailure { get; set; }

    public bool IdentityIsAmbiguous { get; set; }

    public int ImportedVideoId { get; set; } = 1;

    public IReadOnlyList<string> LibraryRoots => roots;

    public IReadOnlyList<string> ConfiguredMetadataEndpoints => ConfiguredEndpoints;

    // Answered here the way the port answers it, never raised: the containment lives in the port, and
    // a fake that raised would be standing in for the host rather than for the seam.
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

    public List<(int VideoId, string KeptPath)> Detached { get; } = [];

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
