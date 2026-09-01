using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace WhisparrSync.Import;

/// <inheritdoc cref="ICoveLibraryPort"/>
/// <remarks>
/// The scan and metadata services are optional. An extension container builds its own copy of the
/// host's scoped registrations, and a container that cannot produce one must still load the
/// extension and report the fact rather than fail every request behind an unresolved dependency.
/// <para>
/// It binds to the base <see cref="DbContext"/> rather than the host's own context type: this
/// extension compiles against the host's entity assembly but not against the assembly that type
/// lives in, and the host registers its context resolvable as the base type, so the overridden save
/// - and the derived numbers it recomputes - still runs at runtime.
/// </para>
/// <para>
/// One instance wraps one scope's context. Every read is a query, never a walk of a navigation
/// property: a read that reached rows through a tracked entity would depend on what else the scope
/// had already loaded.
/// </para>
/// </remarks>
internal sealed class CoveLibraryPort(
    DbContext db,
    IScanService? scan,
    IMetadataServerService? metadata,
    CoveConfiguration? config,
    ILogger log) : ICoveLibraryPort
{
    public IReadOnlyList<string> LibraryRoots => ReadLibraryRoots(config);

    public IReadOnlyList<string> ConfiguredMetadataEndpoints
        => config is null
            ? []
            : [.. config.Scraping.MetadataServers
                .Select(server => server.Endpoint)
                .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))];

    public async Task<LibraryImport> ImportVideoAsync(string path, int? videoId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (scan is null)
        {
            return new LibraryImport(false, null);
        }

        try
        {
            return new LibraryImport(
                true,
                await scan.ImportDownloadedVideoAsync(path, videoId, ct).ConfigureAwait(false));
        }
        // The two the host's own import raises: the file is gone by the time it looks, and the row it
        // resolved is claimed by no item. Both would otherwise leave a route answering outside its
        // declared results and a background walk unable to reach its mark. Narrow on purpose - a
        // broader catch here would hide a defect rather than contain a known refusal.
        catch (Exception refused) when (refused is FileNotFoundException or InvalidOperationException)
        {
            WhisparrSyncLog.HostImportContained(log, refused);
            return new LibraryImport(false, null);
        }
    }

    public async Task<int> DetachSupersededFilesAsync(int videoId, string keptPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keptPath);

        // Tracked, because these rows are about to be written. The set is one item's files.
        var superseded = await db.Set<VideoFile>()
            .Where(file => file.VideoId == videoId && file.Path != keptPath)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (superseded.Count == 0)
        {
            return 0;
        }

        foreach (var file in superseded)
        {
            file.VideoId = null;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return superseded.Count;
    }

    public bool StartFollowUpScan(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (scan is null || paths.Count == 0)
        {
            return scan is not null;
        }

        scan.StartScan(new ScanOperationOptions
        {
            Paths = [.. paths],
            IncludeUnchangedFilesInAssetGeneration = true,
        });
        return true;
    }

    public async Task<HeldFile?> HeldFileAtAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Answered from how many rows came back rather than from the projected value. The video key
        // is nullable, so a single projected key cannot say whether there was a row to project.
        var keys = await db.Set<VideoFile>()
            .AsNoTracking()
            .Where(file => file.Path == path)
            .Select(file => file.VideoId)
            .Take(1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return keys.Count == 0 ? null : new HeldFile(keys[0]);
    }

    public async Task<IdentityResolution> ResolveByRemoteIdAsync(
        string endpoint, string remoteId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);

        // Narrowed on the identifier in the query and on the endpoint in memory. The endpoint rule is
        // the host's, which no provider can translate, and the identifier is what bounds the rows: a
        // filter applied after loading every row would be linear in the library.
        var carriers = await db.Set<VideoRemoteId>()
            .AsNoTracking()
            .Where(row => row.RemoteId == remoteId)
            .Select(row => new { row.VideoId, row.Endpoint })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var videoIds = carriers
            .Where(row => EndpointMatchGuard.SameSource(row.Endpoint, endpoint))
            .Select(row => row.VideoId)
            .Distinct()
            .Take(2)
            .ToList();

        return videoIds.Count switch
        {
            0 => IdentityResolution.Unmatched,
            1 => IdentityResolution.At(videoIds[0]),
            _ => IdentityResolution.TooMany,
        };
    }

    public async Task<bool> CarriesIdentityAsync(int videoId, string endpoint, CancellationToken ct)
    {
        var stored = await db.Set<VideoRemoteId>()
            .AsNoTracking()
            .Where(row => row.VideoId == videoId)
            .Select(row => row.Endpoint)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return stored.Exists(spelling => EndpointMatchGuard.SameSource(spelling, endpoint));
    }

    public async Task<bool> StampIdentityAsync(
        int videoId, string endpoint, string remoteId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);

        if (await CarriesIdentityAsync(videoId, endpoint, ct).ConfigureAwait(false))
        {
            return false;
        }

        db.Set<VideoRemoteId>().Add(new VideoRemoteId
        {
            VideoId = videoId,
            Endpoint = endpoint,
            RemoteId = remoteId,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> EnrichAsync(
        int videoId, string endpoint, string remoteId, CancellationToken ct)
    {
        if (metadata is null)
        {
            return false;
        }

        // Tracked, and with the identity rows loaded: the host's merge mutates the entity it is
        // handed and reads that collection to decide whether to add a row of its own. It saves
        // nothing, so the save below is what commits both.
        var video = await db.Set<Video>()
            .Include(item => item.RemoteIds)
            .FirstOrDefaultAsync(item => item.Id == videoId, ct)
            .ConfigureAwait(false);
        if (video is null)
        {
            return false;
        }

        if (!await metadata.MergeVideoAsync(video, endpoint, remoteId, null, ct).ConfigureAwait(false))
        {
            return false;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The one reading of the host's configured library paths this extension works from: blank
    /// entries dropped, absent configuration an empty list.
    /// </summary>
    /// <remarks>
    /// Static because the port is not the only caller: the pure candidate arithmetic is tested
    /// against the same normalisation with no container and no configuration object to hand, and a
    /// second projection there could disagree about a blank entry.
    /// </remarks>
    internal static IReadOnlyList<string> ReadLibraryRoots(CoveConfiguration? config)
        => config is null
            ? []
            : [.. config.CovePaths
                .Select(path => path.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))];
}
