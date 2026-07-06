using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Renamer.Planner;

namespace Renamer.Execution;

/// <summary>
/// The ONE class that speaks to a live Cove database. It implements <see cref="IRenamerDataPort"/>
/// (the planner's read seam) AND exposes the executor-facing primitives the mutating
/// <c>RenamerExecutor</c> composes: load a tracked file row, re-check a collision, resolve-or-create
/// a destination folder, and apply Basename/ParentFolderId/caption mutations with a single
/// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>. It sets <c>Basename</c>/
/// <c>ParentFolderId</c> ONLY — NEVER <c>BaseFileEntity.Path</c>, which Cove's
/// <c>ComputeFilePaths</c> recomputes inside the overridden save.
///
/// WHY <see cref="DbContext"/> and not the concrete <c>CoveContext</c>: the production
/// <c>Renamer.csproj</c> references <c>Cove.Plugins</c>/<c>Cove.Sdk</c> (compile-time, runtime
/// excluded) which transitively expose <c>Cove.Core</c> entities + EF Core — but NOT
/// <c>Cove.Data</c> where <c>CoveContext</c> lives. The host registers its <c>CoveContext</c> in DI
/// resolvable as the base <see cref="DbContext"/> (its <c>ExtensionManager</c> resolves
/// <c>GetService&lt;DbContext&gt;()</c>), so this class works against the base type +
/// <c>db.Set&lt;BaseFileEntity&gt;()</c>. The overridden <c>SaveChangesAsync</c> (and its
/// <c>ComputeFilePaths</c>) still runs because the runtime instance is the real <c>CoveContext</c>.
/// Tests inject a SQLite-in-memory <c>CoveContext</c> (which IS-A <see cref="DbContext"/>) directly.
///
/// One instance wraps one context (a scope's context). In production the executor opens a scope per
/// run via <c>IServiceScopeFactory.CreateAsyncScope()</c> and constructs this over the scoped
/// <see cref="DbContext"/>.
/// </summary>
public class CoveRenamerDataPort : IRenamerDataPort
{
    private readonly DbContext _db;

    public CoveRenamerDataPort(DbContext db) => _db = db;

    // ── IRenamerDataPort (planner read seam) ──────────────────────────────────

    /// <summary>
    /// Loads a media item's full file graph (via the EF Include chain) and maps it into the
    /// Renamer-owned <see cref="RenamerEntity"/> DTO. Returns null when the item does not exist.
    /// <para>
    /// Read-only: the result is mapped straight into a DTO and never saved, so every query here is
    /// <c>AsNoTracking()</c> — no change tracker entries, no accidental write-back. This mirrors the
    /// host's own read paths (e.g. <c>EfExtensionStore</c>'s <c>GetAsync</c>/<c>GetAllAsync</c>). The
    /// mutating executor re-loads tracked rows separately in <see cref="ApplyAndSaveAsync"/>.
    /// </para>
    /// </summary>
    public async Task<RenamerEntity?> LoadEntityAsync(RenamerFileKind kind, int entityId, CancellationToken ct = default)
    {
        switch (kind)
        {
            case RenamerFileKind.Video:
                {
                    var v = await _db.Set<Video>()
                        .AsNoTracking()
                        .Include(x => x.Studio).ThenInclude(s => s!.Parent).ThenInclude(s => s!.Parent).ThenInclude(s => s!.Parent)
                        .Include(x => x.Files).ThenInclude(f => f.ParentFolder)
                        .Include(x => x.Files).ThenInclude(f => f.Captions)
                        .Include(x => x.VideoPerformers).ThenInclude(vp => vp.Performer)
                        .Include(x => x.VideoTags).ThenInclude(vt => vt.Tag)
                        .FirstOrDefaultAsync(x => x.Id == entityId, ct);
                    if (v is null)
                    {
                        return null;
                    }

                    return new RenamerEntity(
                        v.Id, RenamerFileKind.Video, v.Title, v.Code, v.Studio?.Name, v.Date, v.Organized,
                        [.. v.VideoPerformers
                            .Where(p => p.Performer is not null && p.Performer.Name.Length > 0)
                            .Select(p => new RenamerPerformer(p.Performer!.Id, p.Performer.Name, p.Performer.Favorite, p.Performer.Gender?.ToString()))],
                        [.. v.VideoTags.Select(t => t.Tag?.Name ?? "").Where(n => n.Length > 0)],
                        [.. v.Files.Select(MapVideoFile)],
                        StudioId: v.StudioId,
                        ParentStudios: WalkParentStudios(v.Studio),
                        Director: v.Director);
                }
            case RenamerFileKind.Image:
                {
                    var i = await _db.Set<Image>()
                        .AsNoTracking()
                        .Include(x => x.Studio).ThenInclude(s => s!.Parent).ThenInclude(s => s!.Parent).ThenInclude(s => s!.Parent)
                        .Include(x => x.Files).ThenInclude(f => f.ParentFolder)
                        .Include(x => x.ImagePerformers).ThenInclude(ip => ip.Performer)
                        .Include(x => x.ImageTags).ThenInclude(it => it.Tag)
                        .FirstOrDefaultAsync(x => x.Id == entityId, ct);
                    if (i is null)
                    {
                        return null;
                    }

                    return new RenamerEntity(
                        i.Id, RenamerFileKind.Image, i.Title, i.Code, i.Studio?.Name, i.Date, i.Organized,
                        [.. i.ImagePerformers
                            .Where(p => p.Performer is not null && p.Performer.Name.Length > 0)
                            .Select(p => new RenamerPerformer(p.Performer!.Id, p.Performer.Name, p.Performer.Favorite, p.Performer.Gender?.ToString()))],
                        [.. i.ImageTags.Select(t => t.Tag?.Name ?? "").Where(n => n.Length > 0)],
                        [.. i.Files.Select(MapImageFile)],
                        StudioId: i.StudioId,
                        ParentStudios: WalkParentStudios(i.Studio));
                }
            case RenamerFileKind.Audio:
                {
                    var a = await _db.Set<Audio>()
                        .AsNoTracking()
                        .Include(x => x.Studio).ThenInclude(s => s!.Parent).ThenInclude(s => s!.Parent).ThenInclude(s => s!.Parent)
                        .Include(x => x.Files).ThenInclude(f => f.ParentFolder)
                        .Include(x => x.AudioPerformers).ThenInclude(ap => ap.Performer)
                        .Include(x => x.AudioTags).ThenInclude(at => at.Tag)
                        .FirstOrDefaultAsync(x => x.Id == entityId, ct);
                    if (a is null)
                    {
                        return null;
                    }

                    return new RenamerEntity(
                        a.Id, RenamerFileKind.Audio, a.Title, a.Code, a.Studio?.Name, a.Date, a.Organized,
                        [.. a.AudioPerformers
                            .Where(p => p.Performer is not null && p.Performer.Name.Length > 0)
                            .Select(p => new RenamerPerformer(p.Performer!.Id, p.Performer.Name, p.Performer.Favorite, p.Performer.Gender?.ToString()))],
                        [.. a.AudioTags.Select(t => t.Tag?.Name ?? "").Where(n => n.Length > 0)],
                        [.. a.Files.Select(MapAudioFile)],
                        StudioId: a.StudioId,
                        ParentStudios: WalkParentStudios(a.Studio));
                }
            default:
                // Gallery is not yet a renamable kind.
                return null;
        }
    }

    /// <summary>
    /// An <c>AsNoTracking</c> id-only bulk query over the kind's table — Gallery (and any other
    /// non-renamable kind) returns empty rather than throwing, mirroring <see cref="LoadEntityAsync"/>'s
    /// own treatment of Gallery as "not yet a renamable kind."
    /// </summary>
    public async Task<IReadOnlyList<int>> LoadAllEntityIdsAsync(RenamerFileKind kind, CancellationToken ct = default)
    {
        return kind switch
        {
            RenamerFileKind.Video => await _db.Set<Video>().AsNoTracking().Select(v => v.Id).ToArrayAsync(ct),
            RenamerFileKind.Image => await _db.Set<Image>().AsNoTracking().Select(i => i.Id).ToArrayAsync(ct),
            RenamerFileKind.Audio => await _db.Set<Audio>().AsNoTracking().Select(a => a.Id).ToArrayAsync(ct),
            _ => [],
        };
    }

    /// <summary>
    /// The <c>(ParentFolderId, Basename)</c> unique-index pre-check: true iff some OTHER file row
    /// already occupies the slot.
    /// </summary>
    public virtual async Task<bool> CollisionExistsAsync(int folderId, string basename, int selfFileId, CancellationToken ct = default)
        => await _db.Set<BaseFileEntity>()
            .AnyAsync(f => f.ParentFolderId == folderId && f.Basename == basename && f.Id != selfFileId, ct);

    /// <summary>
    /// Resolves a destination <see cref="Folder"/> by its (forward-slash) path, creating it (with
    /// its own <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> to obtain the Id) when
    /// absent — mirrors Cove's own <c>FileOpsController.MoveFiles</c>.
    /// </summary>
    public async Task<int> GetOrCreateFolderIdAsync(string folderPath, CancellationToken ct = default)
        => (await GetOrCreateFolderAsync(folderPath, ct)).Id;

    /// <summary>
    /// Read-only counterpart to <see cref="GetOrCreateFolderIdAsync"/>: returns the existing folder's
    /// id or <c>null</c> when absent. Same path normalization and lookup as
    /// <see cref="GetOrCreateFolderAsync"/>, but it never <c>Add</c>s or
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>s — so the planner can run a
    /// dry-run preview without persisting a destination folder.
    /// </summary>
    public async Task<int?> TryGetFolderIdAsync(string folderPath, CancellationToken ct = default)
    {
        var normalized = folderPath.Replace('\\', '/');
        var existing = await _db.Set<Folder>().AsNoTracking()
            .FirstOrDefaultAsync(f => normalized == f.Path, ct);
        return existing?.Id;
    }

    /// <summary>The read-only disk probe backing the preview's missing-source warning; never mutates.</summary>
    public Task<bool> SourceExistsAsync(string fullPath, CancellationToken ct = default)
    {
        var native = fullPath.Replace('/', Path.DirectorySeparatorChar);
        return Task.FromResult(System.IO.File.Exists(native));
    }

    /// <summary>
    /// Persists a planned set of mutations via <see cref="ApplyAndSaveAsync"/>. Provided so the
    /// planner-facing seam is complete; the executor uses the richer <see cref="ApplyAndSaveAsync"/>
    /// directly so it can read back the recomputed paths.
    /// </summary>
    public async Task<int> SaveAsync(IReadOnlyList<RenamerFileMutation> mutations, CancellationToken ct = default)
        => (await ApplyAndSaveAsync(mutations, ct)).Count;

    // ── Executor-facing primitives ───────────────────────────────────────────

    /// <summary>The recomputed identity of a saved file row, read back after a save for the Path assertion + event.</summary>
    /// <param name="FileId">The file row id.</param>
    /// <param name="RecomputedPath">The <c>BaseFileEntity.Path</c> Cove recomputed on save (forward-slash).</param>
    public readonly record struct SavedFile(int FileId, string RecomputedPath);

    /// <summary>Resolves an existing <see cref="Folder"/> by path or creates+saves one for its Id. Returns the tracked entity.</summary>
    public async Task<Folder> GetOrCreateFolderAsync(string folderPath, CancellationToken ct = default)
    {
        var normalized = folderPath.Replace('\\', '/');
        var existing = await _db.Set<Folder>().FirstOrDefaultAsync(f => normalized == f.Path, ct);
        if (existing is not null)
        {
            return existing;
        }

        var folder = new Folder { Path = normalized, ModTime = DateTime.UtcNow };
        _db.Set<Folder>().Add(folder);
        await _db.SaveChangesAsync(ct);  // obtain the Id, mirroring FileOpsController
        return folder;
    }

    /// <summary>
    /// Applies each mutation to its tracked file row — sets <c>Basename</c>, optionally
    /// <c>ParentFolderId</c> + the <c>ParentFolder</c> navigation (so the recompute resolves the new
    /// folder path), and each moved caption's <c>Filename</c> — then calls a single
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>. NEVER sets <c>Path</c>. Returns
    /// the recomputed Path of each saved file. Throws on a save failure (e.g. the unique-index
    /// violation) so the executor's catch can roll the disk back.
    /// </summary>
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

            file.Basename = m.NewBasename;     // NEVER file.Path — ComputeFilePaths recomputes it.

            if (m.NewParentFolderId is int newFolderId && newFolderId != file.ParentFolderId)
            {
                file.ParentFolderId = newFolderId;
                // Set the navigation too so ComputeFilePaths resolves the new folder path in-memory.
                file.ParentFolder = await _db.Set<Folder>().FirstOrDefaultAsync(f => f.Id == newFolderId, ct);
            }

            if (m.CaptionRenames is { Count: > 0 } && file is VideoFile vf)
            {
                foreach (var (captionId, newFilename) in m.CaptionRenames)
                {
                    var cap = vf.Captions.FirstOrDefault(c => c.Id == captionId);
                    if (cap is not null)
                    {
                        cap.Filename = newFilename;
                    }
                }
            }

            touched.Add(file);
        }

        await _db.SaveChangesAsync(ct);  // ComputeFilePaths recomputes every touched file's Path here.

        return [.. touched.Select(f => new SavedFile(f.Id, f.Path))];
    }

    // ── DTO mapping ──────────────────────────────────────────────────────────

    /// <summary>
    /// Walks the loaded studio's parent navigation into the Renamer-owned, NEAREST-FIRST
    /// <c>(int Id, string Name)</c> tuple chain: index 0 is <paramref name="studio"/>'s immediate
    /// parent, walking toward the root. The walk is bounded to <c>MaxParentDepth</c> ancestor hops,
    /// and the eager-load <c>.Include(...).ThenInclude(Parent)</c> chain in <see cref="LoadEntityAsync"/>
    /// loads exactly that many ancestor levels, so the walk never references an ancestor that was not
    /// loaded. The cap is a deliberate hard product limit on studio-hierarchy depth: an ancestor beyond
    /// it simply goes unmatched (equivalent to no rule), and a pathological self-referencing chain can
    /// never loop unbounded. Returns an empty list when the studio is null or top-level (no parent).
    /// <c>Cove.Core.Entities.Studio</c> is touched ONLY here; the boundary holds because this returns
    /// the Renamer-owned tuple shape, never the Cove type.
    /// </summary>
    private static List<(int Id, string Name)> WalkParentStudios(Studio? studio)
    {
        // Keep this in lockstep with the number of ".ThenInclude(s => s!.Parent)" hops loaded after
        // ".Include(x => x.Studio)" in LoadEntityAsync — currently three ancestor levels.
        const int MaxParentDepth = 3;
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
}
