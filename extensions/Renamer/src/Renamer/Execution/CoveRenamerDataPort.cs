using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Renamer.Planner;

namespace Renamer.Execution;

// The one class that speaks to a live Cove database. It is the planner's read seam and the
// executor's write primitives: load a tracked file row, re-check a collision, resolve or create a
// destination folder, and apply Basename, ParentFolderId and caption mutations under one
// SaveChangesAsync. It sets Basename and ParentFolderId and never BaseFileEntity.Path, which Cove's
// ComputeFilePaths recomputes inside the overridden save.
//
// It works against the base DbContext and not the concrete CoveContext. Renamer.csproj references
// Cove.Plugins and Cove.Sdk, which expose Cove.Core entities and EF Core but not the Cove.Data
// assembly where CoveContext lives. The host registers its CoveContext resolvable as DbContext, so
// this class reaches its rows through db.Set<BaseFileEntity>(). The overridden SaveChangesAsync, and
// with it ComputeFilePaths, still runs because the runtime instance is the real CoveContext. Tests
// inject a SQLite in-memory CoveContext directly.
//
// One instance wraps one scope's context, and a DbContext is not thread-safe. In production the
// executor opens a scope per run with IServiceScopeFactory.CreateAsyncScope and constructs this over
// that scoped context.
public class CoveRenamerDataPort : IRenamerDataPort
{
    private readonly DbContext _db;
    private readonly CoveConfiguration? _config;

    // config is Cove's own configuration singleton, the source of LibraryRoots. It is optional so a
    // caller with no library map can construct the port; omitting it in production is visible, because
    // an item with a folder template then plans as SkipUnanchored and says so.
    public CoveRenamerDataPort(DbContext db, CoveConfiguration? config = null)
    {
        _db = db;
        _config = config;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> LibraryRoots => ReadLibraryRoots(_config);

    // The one reading of CoveConfiguration.CovePaths into the library paths this extension works from:
    // blank entries dropped, each survivor spelled canonically, absent configuration an empty list.
    //
    // Static and shared because the port is not the only caller: the one-time options conversion runs at
    // initialize, before any scope or port exists, and has to place stored rules under the same paths
    // the planner later anchors on. A second projection there could disagree about a blank entry, which
    // is the difference between preserving a rule and dropping it.
    public static IReadOnlyList<string> ReadLibraryRoots(CoveConfiguration? config) =>
        config is null
            ? []
            : [.. config.CovePaths
                .Select(p => p.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(Canonical)];

    // The one spelling of a Cove library path this extension uses: forward slashes, no trailing
    // separator.
    //
    // Cove hands its paths back in the platform's own spelling, and this list leaves over the wire: the
    // settings panel stores a destination root as the very string this list gave it and re-checks
    // membership against it, so two spellings of one folder would read as two folders. Normalizing where
    // the host's value enters leaves one spelling for the panel to store and the planner to re-check.
    //
    // PathConfinement.ContainingRoot re-trims the entry it returns, so a root-only library path spelled
    // "/" here comes back from it as "", which is how a destination says "the file's own library path".
    // Reaching that needs a Cove library path spelled exactly /, \ or //, and any longer sibling root
    // wins the longest match first.
    private static string Canonical(string path)
    {
        string normalized = PathOps.NormalizeSlash(path).TrimEnd('/');

        // A path of nothing but separators trims away entirely, and the empty string is not a spelling
        // of a root here: it is how a destination says "the file's own library path".
        return normalized.Length == 0 ? "/" : normalized;
    }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1304:Specify CultureInfo",
        Justification = "ToLower() here is never executed in .NET - it is translated to the provider's " +
            "own lower() in SQL, and the culture-taking overloads have no translation.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1311:Specify a culture",
        Justification = "Same as CA1304: this call is translated to SQL, not run by the CLR.")]
    public async Task<NameResolution> ResolveNamesAsync(
        RenamerEntityKind kind, IReadOnlyList<string> names, CancellationToken ct = default)
    {
        bool hasRows = kind switch
        {
            RenamerEntityKind.Tag => await _db.Set<Tag>().AsNoTracking().AnyAsync(ct),
            RenamerEntityKind.Performer => await _db.Set<Performer>().AsNoTracking().AnyAsync(ct),
            _ => false,
        };

        if (names.Count == 0)
        {
            return new NameResolution(hasRows, []);
        }

        // Both sides are lowercased in the query. The provider decides what a case-insensitive string
        // comparison means, and Postgres's default collation is case-sensitive, so an EF
        // `string.Equals(a, b, OrdinalIgnoreCase)` does not translate and a plain `Contains` would miss
        // the case variants this resolution exists to find.
        var wanted = names.Select(n => n.ToLowerInvariant()).Distinct().ToList();

        IReadOnlyList<(int Id, string Name)> matches = kind switch
        {
            RenamerEntityKind.Tag => await _db.Set<Tag>().AsNoTracking()
                .Where(t => wanted.Contains(t.Name.ToLower()))
                .Select(t => new ValueTuple<int, string>(t.Id, t.Name))
                .ToListAsync(ct),
            RenamerEntityKind.Performer => await _db.Set<Performer>().AsNoTracking()
                .Where(p => wanted.Contains(p.Name.ToLower()))
                .Select(p => new ValueTuple<int, string>(p.Id, p.Name))
                .ToListAsync(ct),
            _ => [],
        };

        return new NameResolution(hasRows, matches);
    }

    // Returns null when the item does not exist. The result is mapped straight into a DTO and never
    // saved, so every query here is AsNoTracking: no change tracker entries and no write-back. The
    // mutating path re-loads tracked rows separately in ApplyAndSaveAsync.
    public async Task<RenamerEntity?> LoadEntityAsync(RenamerFileKind kind, int entityId, CancellationToken ct = default)
    {
        switch (kind)
        {
            case RenamerFileKind.Video:
                {
                    var v = await VideoQuery().FirstOrDefaultAsync(x => x.Id == entityId, ct);
                    return v is null ? null : MapVideoEntity(v);
                }
            case RenamerFileKind.Image:
                {
                    var i = await ImageQuery().FirstOrDefaultAsync(x => x.Id == entityId, ct);
                    return i is null ? null : MapImageEntity(i);
                }
            case RenamerFileKind.Audio:
                {
                    var a = await AudioQuery().FirstOrDefaultAsync(x => x.Id == entityId, ct);
                    return a is null ? null : MapAudioEntity(a);
                }
            case RenamerFileKind.Text:
                {
                    var t = await TextQuery().FirstOrDefaultAsync(x => x.Id == entityId, ct);
                    return t is null ? null : MapTextEntity(t);
                }
            default:
                // Gallery is not yet a renamable kind.
                return null;
        }
    }

    // EF Core translates an in list to one bound parameter per id, so an unchunked load would exceed
    // Postgres's parameter cap and generate pathological SQL. Chunking keeps the parameter count bounded
    // at one round-trip per chunk. The in list is provider-agnostic: the provider is host-supplied,
    // Postgres in production and SQLite in tests, and a raw Npgsql array parameter would not translate
    // on SQLite.
    internal const int LoadChunkSize = 200;

    // The single source of truth for studio-hierarchy depth. Two things stay bound to it: the
    // WalkParentStudios ancestor-hop bound, and the number of ".ThenInclude(s => s!.Parent)" hops each
    // per-kind query below carries, because EF's Studio-to-Studio ThenInclude cannot be parameterized by
    // a runtime count. StudioDepthLockstepTests seeds one more ancestor than this depth and asserts how
    // many load through the real chain, so changing the constant or a query's hop count alone fails it.
    internal const int MaxParentDepth = 3;

    public async Task<IReadOnlyList<RenamerEntity>> LoadEntitiesAsync(
        RenamerFileKind kind, IReadOnlyList<int> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var result = new List<RenamerEntity>(ids.Count);
        switch (kind)
        {
            case RenamerFileKind.Video:
                foreach (var chunk in ids.Chunk(LoadChunkSize))
                {
                    var rows = await VideoQuery().Where(x => chunk.Contains(x.Id)).ToListAsync(ct);
                    result.AddRange(rows.Select(MapVideoEntity));
                }
                break;
            case RenamerFileKind.Image:
                foreach (var chunk in ids.Chunk(LoadChunkSize))
                {
                    var rows = await ImageQuery().Where(x => chunk.Contains(x.Id)).ToListAsync(ct);
                    result.AddRange(rows.Select(MapImageEntity));
                }
                break;
            case RenamerFileKind.Audio:
                foreach (var chunk in ids.Chunk(LoadChunkSize))
                {
                    var rows = await AudioQuery().Where(x => chunk.Contains(x.Id)).ToListAsync(ct);
                    result.AddRange(rows.Select(MapAudioEntity));
                }
                break;
            case RenamerFileKind.Text:
                foreach (var chunk in ids.Chunk(LoadChunkSize))
                {
                    var rows = await TextQuery().Where(x => chunk.Contains(x.Id)).ToListAsync(ct);
                    result.AddRange(rows.Select(MapTextEntity));
                }
                break;
            default:
                // Gallery is not yet a renamable kind.
                return [];
        }

        return result;
    }

    // Single-load and batch-load share the per-kind query and its mapper below, so their DTOs cannot
    // drift apart.

    // Each query's ancestor Include hop count is bound to MaxParentDepth and guarded by
    // StudioDepthLockstepTests: add or drop a ".ThenInclude(s => s!.Parent)" here without matching the
    // constant and that test fails.

    // AsSplitQuery on every one of them: each query includes sibling collections (files, performers,
    // tags), and a single-statement join returns their product per entity. One row per file times one
    // per performer times one per tag is a row count that grows with the library even though the
    // entity count does not.
    private IQueryable<Video> VideoQuery() => _db.Set<Video>()
        .AsNoTracking()
        .Include(x => x.Studio).ThenInclude(s => s!.Parent).ThenInclude(s => s!.Parent).ThenInclude(s => s!.Parent)
        .Include(x => x.Files).ThenInclude(f => f.ParentFolder)
        .Include(x => x.Files).ThenInclude(f => f.Captions)
        .Include(x => x.VideoPerformers).ThenInclude(vp => vp.Performer)
        .Include(x => x.VideoTags).ThenInclude(vt => vt.Tag)
        .AsSplitQuery();

    private IQueryable<Image> ImageQuery() => _db.Set<Image>()
        .AsNoTracking()
        .Include(x => x.Studio).ThenInclude(s => s!.Parent).ThenInclude(s => s!.Parent).ThenInclude(s => s!.Parent)
        .Include(x => x.Files).ThenInclude(f => f.ParentFolder)
        .Include(x => x.ImagePerformers).ThenInclude(ip => ip.Performer)
        .Include(x => x.ImageTags).ThenInclude(it => it.Tag)
        .AsSplitQuery();

    private IQueryable<Audio> AudioQuery() => _db.Set<Audio>()
        .AsNoTracking()
        .Include(x => x.Studio).ThenInclude(s => s!.Parent).ThenInclude(s => s!.Parent).ThenInclude(s => s!.Parent)
        .Include(x => x.Files).ThenInclude(f => f.ParentFolder)
        .Include(x => x.AudioPerformers).ThenInclude(ap => ap.Performer)
        .Include(x => x.AudioTags).ThenInclude(at => at.Tag)
        .AsSplitQuery();

    private IQueryable<TextDocument> TextQuery() => _db.Set<TextDocument>()
        .AsNoTracking()
        .Include(x => x.Studio).ThenInclude(s => s!.Parent).ThenInclude(s => s!.Parent).ThenInclude(s => s!.Parent)
        .Include(x => x.Files).ThenInclude(f => f.ParentFolder)
        .Include(x => x.TextPerformers).ThenInclude(tp => tp.Performer)
        .Include(x => x.TextTags).ThenInclude(tt => tt.Tag)
        .AsSplitQuery();

    private static RenamerEntity MapVideoEntity(Video v) => new(
        v.Id, RenamerFileKind.Video, v.Title, v.Code, v.Studio?.Name, v.Date, v.Organized,
        [.. v.VideoPerformers
            .Where(p => p.Performer is not null && p.Performer.Name.Length > 0)
            .Select(p => new RenamerPerformer(p.Performer!.Id, p.Performer.Name, p.Performer.Favorite, p.Performer.Gender?.ToString()))],
        [.. v.VideoTags.Where(t => t.Tag is not null && t.Tag.Name.Length > 0).Select(t => (t.Tag!.Id, t.Tag.Name))],
        [.. v.Files.Select(MapVideoFile)],
        StudioId: v.StudioId,
        ParentStudios: WalkParentStudios(v.Studio),
        Director: v.Director);

    private static RenamerEntity MapImageEntity(Image i) => new(
        i.Id, RenamerFileKind.Image, i.Title, i.Code, i.Studio?.Name, i.Date, i.Organized,
        [.. i.ImagePerformers
            .Where(p => p.Performer is not null && p.Performer.Name.Length > 0)
            .Select(p => new RenamerPerformer(p.Performer!.Id, p.Performer.Name, p.Performer.Favorite, p.Performer.Gender?.ToString()))],
        [.. i.ImageTags.Where(t => t.Tag is not null && t.Tag.Name.Length > 0).Select(t => (t.Tag!.Id, t.Tag.Name))],
        [.. i.Files.Select(MapImageFile)],
        StudioId: i.StudioId,
        ParentStudios: WalkParentStudios(i.Studio));

    private static RenamerEntity MapAudioEntity(Audio a) => new(
        a.Id, RenamerFileKind.Audio, a.Title, a.Code, a.Studio?.Name, a.Date, a.Organized,
        [.. a.AudioPerformers
            .Where(p => p.Performer is not null && p.Performer.Name.Length > 0)
            .Select(p => new RenamerPerformer(p.Performer!.Id, p.Performer.Name, p.Performer.Favorite, p.Performer.Gender?.ToString()))],
        [.. a.AudioTags.Where(t => t.Tag is not null && t.Tag.Name.Length > 0).Select(t => (t.Tag!.Id, t.Tag.Name))],
        [.. a.Files.Select(MapAudioFile)],
        StudioId: a.StudioId,
        ParentStudios: WalkParentStudios(a.Studio));

    private static RenamerEntity MapTextEntity(TextDocument t) => new(
        t.Id, RenamerFileKind.Text, t.Title, t.Code, t.Studio?.Name, t.Date, t.Organized,
        [.. t.TextPerformers
            .Where(p => p.Performer is not null && p.Performer.Name.Length > 0)
            .Select(p => new RenamerPerformer(p.Performer!.Id, p.Performer.Name, p.Performer.Favorite, p.Performer.Gender?.ToString()))],
        [.. t.TextTags.Where(x => x.Tag is not null && x.Tag.Name.Length > 0).Select(x => (x.Tag!.Id, x.Tag.Name))],
        [.. t.Files.Select(MapTextFile)],
        StudioId: t.StudioId,
        ParentStudios: WalkParentStudios(t.Studio));

    /// <inheritdoc />
    public async Task<int> CountEntitiesAsync(RenamerFileKind kind, CancellationToken ct = default) => kind switch
    {
        RenamerFileKind.Video => await _db.Set<Video>().AsNoTracking().CountAsync(ct),
        RenamerFileKind.Image => await _db.Set<Image>().AsNoTracking().CountAsync(ct),
        RenamerFileKind.Audio => await _db.Set<Audio>().AsNoTracking().CountAsync(ct),
        RenamerFileKind.Text => await _db.Set<TextDocument>().AsNoTracking().CountAsync(ct),
        _ => 0,
    };

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, int>> CountSourcePathClaimsAsync(
        IReadOnlyList<string> sourcePaths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);

        var claims = new Dictionary<string, int>(PathOps.PathComparer);

        // Chunked for the same reason LoadEntitiesAsync is: EF binds one parameter per element of an in
        // list, and a whole run of planned paths would approach the provider's parameter cap.
        foreach (var chunk in sourcePaths.Distinct(PathOps.PathComparer).Chunk(LoadChunkSize))
        {
            // Where the volume treats a path and its case-variant as one file, so must this query.
            // Equality here is the database collation's, and a case-sensitive collation over a
            // case-insensitive volume would read a twin row differing only in case as a second path
            // and report neither as contested. Cove indexes upper(Path), so an index serves the folded
            // comparison.
            var rows = PathOps.PathsIgnoreCase
                ? await FoldedClaimsAsync(chunk, ct)
                : await ExactClaimsAsync(chunk, ct);

            foreach (var row in rows)
            {
                claims[row.Path] = row.Claims;
            }
        }

        return claims;
    }

    private sealed record PathClaims(string Path, int Claims);

    private Task<List<PathClaims>> ExactClaimsAsync(string[] paths, CancellationToken ct) =>
        _db.Set<BaseFileEntity>().AsNoTracking()
            .Where(f => paths.Contains(f.Path))
            .GroupBy(f => f.Path)
            .Where(g => g.Count() > 1)
            .Select(g => new PathClaims(g.Key, g.Count()))
            .ToListAsync(ct);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1304:Specify CultureInfo",
        Justification = "ToUpper() here is never executed in .NET - it is translated to the provider's " +
            "own upper() in SQL, and the culture-taking overloads have no translation.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1311:Specify a culture",
        Justification = "Same as CA1304: this call is translated to SQL, not run by the CLR.")]
    private Task<List<PathClaims>> FoldedClaimsAsync(string[] paths, CancellationToken ct)
    {
        // The keys come back folded. The caller's dictionary compares with PathOps.PathComparer, which
        // ignores case on exactly the platforms this branch runs on, so a folded key still answers a
        // lookup by the path as planned.
        var folded = Array.ConvertAll(paths, p => p.ToUpperInvariant());

        return _db.Set<BaseFileEntity>().AsNoTracking()
            .Where(f => folded.Contains(f.Path.ToUpper()))
            .GroupBy(f => f.Path.ToUpper())
            .Where(g => g.Count() > 1)
            .Select(g => new PathClaims(g.Key, g.Count()))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> LoadEntityIdPageAsync(
        RenamerFileKind kind, int afterEntityId, int take, CancellationToken ct = default)
    {
        // The ORDER BY below is load-bearing: it is the total order the caller's cursor rests on.
        // In provider order a cursor skips and repeats entities across pages as rows change.
        if (take <= 0)
        {
            return [];
        }

        return kind switch
        {
            RenamerFileKind.Video => await _db.Set<Video>().AsNoTracking()
                .Where(v => v.Id > afterEntityId).OrderBy(v => v.Id).Take(take).Select(v => v.Id).ToArrayAsync(ct),
            RenamerFileKind.Image => await _db.Set<Image>().AsNoTracking()
                .Where(i => i.Id > afterEntityId).OrderBy(i => i.Id).Take(take).Select(i => i.Id).ToArrayAsync(ct),
            RenamerFileKind.Audio => await _db.Set<Audio>().AsNoTracking()
                .Where(a => a.Id > afterEntityId).OrderBy(a => a.Id).Take(take).Select(a => a.Id).ToArrayAsync(ct),
            RenamerFileKind.Text => await _db.Set<TextDocument>().AsNoTracking()
                .Where(t => t.Id > afterEntityId).OrderBy(t => t.Id).Take(take).Select(t => t.Id).ToArrayAsync(ct),
            _ => [],
        };
    }

    // The (ParentFolderId, Basename) unique-index pre-check: true when another file row already
    // occupies the slot. The source row is excluded, so a case-only rename onto itself is not a
    // collision.
    public virtual async Task<bool> CollisionExistsAsync(int folderId, string basename, int selfFileId, CancellationToken ct = default)
        => await _db.Set<BaseFileEntity>()
            .AnyAsync(f => f.ParentFolderId == folderId && f.Basename == basename && f.Id != selfFileId, ct);

    // Resolves a destination folder by its forward-slash path, creating it with its own save to obtain
    // the id when absent, as Cove's own FileOpsController.MoveFiles does.
    public async Task<int> GetOrCreateFolderIdAsync(string folderPath, CancellationToken ct = default)
        => (await GetOrCreateFolderAsync(folderPath, ct)).Id;

    // The read-only counterpart: the existing folder's id, or null when absent. It adds and saves
    // nothing, so a preview does not persist a destination folder.
    public async Task<int?> TryGetFolderIdAsync(string folderPath, CancellationToken ct = default)
    {
        var normalized = folderPath.Replace('\\', '/');
        var existing = await _db.Set<Folder>().AsNoTracking()
            .FirstOrDefaultAsync(f => normalized == f.Path, ct);
        return existing?.Id;
    }

    // The read-only disk probe backing the preview's missing-source warning.
    public Task<bool> SourceExistsAsync(string fullPath, CancellationToken ct = default)
    {
        var native = fullPath.Replace('/', Path.DirectorySeparatorChar);
        return Task.FromResult(System.IO.File.Exists(native));
    }

    // Returns the tracked folder entity, creating and saving one when the path has none.
    private async Task<Folder> GetOrCreateFolderAsync(string folderPath, CancellationToken ct = default)
    {
        var normalized = folderPath.Replace('\\', '/');
        var existing = await _db.Set<Folder>().FirstOrDefaultAsync(f => normalized == f.Path, ct);
        if (existing is not null)
        {
            return existing;
        }

        var folder = new Folder { Path = normalized, ModTime = DateTime.UtcNow };
        _db.Set<Folder>().Add(folder);
        await _db.SaveChangesAsync(ct);  // the row has to be saved for its Id
        return folder;
    }

    // Applies each mutation to its tracked file row: Basename, optionally ParentFolderId and the
    // ParentFolder navigation so the recompute resolves the new folder path, and each moved caption's
    // Filename. Path is never set; Cove recomputes it in the one SaveChangesAsync, whose result is the
    // recomputed Path of each saved file. A save failure, such as the unique-index violation, throws so
    // the executor's catch rolls the disk back.
    //
    // Every caller passes a single mutation today, so no batch shape is exercised: this costs a tracked
    // query per mutation, and another per mutation that changes folder, deduping neither. A batching
    // caller wants the chunked in list of LoadEntitiesAsync.
    public virtual async Task<IReadOnlyList<SavedFile>> ApplyAndSaveAsync(
        IReadOnlyList<RenamerFileMutation> mutations, CancellationToken ct = default)
    {
        var touched = new List<BaseFileEntity>(mutations.Count);

        foreach (var m in mutations)
        {
            var file = await _db.Set<BaseFileEntity>()
                .Include(f => f.ParentFolder)
                .FirstOrDefaultAsync(f => f.Id == m.FileId, ct)
                ?? throw new InvalidOperationException($"file {m.FileId} not found");

            file.Basename = m.NewBasename;     // not file.Path, which ComputeFilePaths recomputes

            if (m.NewParentFolderId is int newFolderId && newFolderId != file.ParentFolderId)
            {
                file.ParentFolderId = newFolderId;
                // The navigation is set too, so ComputeFilePaths resolves the new folder path in memory.
                file.ParentFolder = await _db.Set<Folder>().FirstOrDefaultAsync(f => f.Id == newFolderId, ct);
            }

            if (m.CaptionRenames is { Count: > 0 } && file is VideoFile vf)
            {
                // The rows are queried and not reached through file.Captions. Every read this port makes
                // is AsNoTracking, so in production nothing has put this file's captions in the change
                // tracker and the navigation is empty: a lookup through it finds nothing and each rename
                // is dropped in silence, leaving the row naming a file the move has taken away. A test
                // that seeds a caption through the same context gets the navigation populated by
                // relationship fix-up, so a fixture hides this.
                var captionIds = m.CaptionRenames.Select(cr => cr.CaptionId).ToList();
                var captions = await _db.Set<VideoCaption>()
                    .Where(c => c.FileId == vf.Id && captionIds.Contains(c.Id))
                    .ToListAsync(ct);

                foreach (var (captionId, newFilename) in m.CaptionRenames)
                {
                    var cap = captions.FirstOrDefault(c => c.Id == captionId);
                    if (cap is not null)
                    {
                        cap.Filename = newFilename;
                    }
                }
            }

            if (m.EntityTitle is RenamerEntityTitleWrite titleWrite)
            {
                await ApplyDerivedTitleAsync(titleWrite, ct);
            }

            touched.Add(file);
        }

        await _db.SaveChangesAsync(ct);  // ComputeFilePaths recomputes every touched file's Path here

        return [.. touched.Select(f => new SavedFile(f.Id, f.Path))];
    }

    // Records a filename-derived title on its media entity, and only on one that still has none.
    //
    // This is the one place this extension writes metadata and not location, and the emptiness re-check
    // against the tracked row is what keeps it safe: the planner derives a title only for a title-less
    // item, but a person can type one between the preview and the run, and a rename does not overwrite
    // what they wrote. The same check makes the write idempotent across the files of a multi-file item,
    // which save one at a time. There is no SaveChangesAsync here: the caller's single save carries it,
    // so a recorded title and its rename cannot come apart. MetadataProjector.DerivedTitle covers why it
    // is recorded at all.
    private async Task ApplyDerivedTitleAsync(RenamerEntityTitleWrite write, CancellationToken ct)
    {
        switch (write.Kind)
        {
            case RenamerFileKind.Video:
                var video = await _db.Set<Video>().FirstOrDefaultAsync(x => x.Id == write.EntityId, ct);
                if (video is not null && string.IsNullOrEmpty(video.Title))
                {
                    video.Title = write.Title;
                }

                break;
            case RenamerFileKind.Image:
                var image = await _db.Set<Image>().FirstOrDefaultAsync(x => x.Id == write.EntityId, ct);
                if (image is not null && string.IsNullOrEmpty(image.Title))
                {
                    image.Title = write.Title;
                }

                break;
            case RenamerFileKind.Audio:
                var audio = await _db.Set<Audio>().FirstOrDefaultAsync(x => x.Id == write.EntityId, ct);
                if (audio is not null && string.IsNullOrEmpty(audio.Title))
                {
                    audio.Title = write.Title;
                }

                break;
            case RenamerFileKind.Text:
                var text = await _db.Set<TextDocument>().FirstOrDefaultAsync(x => x.Id == write.EntityId, ct);
                if (text is not null && string.IsNullOrEmpty(text.Title))
                {
                    text.Title = write.Title;
                }

                break;
            default:
                // Gallery is not a renamable kind, so no plan carries one here.
                break;
        }
    }

    // Walks the loaded studio's parent navigation into a nearest-first chain: index 0 is the studio's
    // immediate parent, walking toward the root. The walk is bounded to MaxParentDepth hops, and the
    // Include chain loads exactly that many ancestor levels, so the walk never references an ancestor
    // that was not loaded. The cap is a hard limit on studio-hierarchy depth: an ancestor beyond it goes
    // unmatched, and a self-referencing chain cannot loop unbounded. A null or top-level studio yields
    // an empty list. Cove's Studio type is touched only here, and the tuple shape returned is the
    // renamer's own.
    private static List<(int Id, string Name)> WalkParentStudios(Studio? studio)
    {
        var chain = new List<(int Id, string Name)>(MaxParentDepth);
        var current = studio?.Parent;
        for (int hops = 0; current is not null && hops < MaxParentDepth; hops++)
        {
            chain.Add((current.Id, current.Name));
            current = current.Parent;
        }

        return chain;
    }

    private static RenamerFile MapVideoFile(VideoFile f) => new(
        FileId: f.Id, Kind: RenamerFileKind.Video, Basename: f.Basename,
        ParentFolderId: f.ParentFolderId, ParentFolderPath: f.ParentFolder?.Path ?? "",
        Format: f.Format, Width: f.Width, Height: f.Height, Duration: f.Duration,
        VideoCodec: f.VideoCodec, AudioCodec: f.AudioCodec, FrameRate: f.FrameRate,
        Captions: [.. f.Captions.Select(c => new RenamerCaption(c.Id, c.Filename))],
        SizeBytes: f.Size,
        BitRate: f.BitRate);

    private static RenamerFile MapImageFile(ImageFile f) => new(
        FileId: f.Id, Kind: RenamerFileKind.Image, Basename: f.Basename,
        ParentFolderId: f.ParentFolderId, ParentFolderPath: f.ParentFolder?.Path ?? "",
        Format: f.Format, Width: f.Width, Height: f.Height,
        SizeBytes: f.Size);

    private static RenamerFile MapAudioFile(AudioFile f) => new(
        FileId: f.Id, Kind: RenamerFileKind.Audio, Basename: f.Basename,
        ParentFolderId: f.ParentFolderId, ParentFolderPath: f.ParentFolder?.Path ?? "",
        Format: f.Format, Duration: f.Duration, AudioCodec: f.AudioCodec,
        SizeBytes: f.Size);

    private static RenamerFile MapTextFile(TextFile f) => new(
        FileId: f.Id, Kind: RenamerFileKind.Text, Basename: f.Basename,
        ParentFolderId: f.ParentFolderId, ParentFolderPath: f.ParentFolder?.Path ?? "",
        Format: f.Format,
        SizeBytes: f.Size);
}
