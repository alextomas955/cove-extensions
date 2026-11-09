using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Renamer.Tests.TestSupport;

internal static class ExecutorTestSeed
{
    // A null date leaves $date/$year absent. The $resolution label needs both dimensions, so
    // leaving either height or width at 0 renders none.
    public static async Task<(int folderId, int videoId, int fileId)> SeedVideoAsync(
        DbContext db, string folderPath, string basename, string title,
        bool organized = true, DateOnly? date = null, int height = 0, int width = 0,
        long size = 0, CancellationToken ct = default)
    {
        var folder = new Folder { Path = folderPath.Replace('\\', '/'), ModTime = DateTime.UtcNow };
        db.Set<Folder>().Add(folder);
        await db.SaveChangesAsync(ct);

        var video = new Video { Title = title, Organized = organized, Date = date };
        db.Set<Video>().Add(video);
        await db.SaveChangesAsync(ct);

        var file = new VideoFile
        {
            Basename = basename,
            ParentFolderId = folder.Id,
            Format = ExtOf(basename),
            VideoId = video.Id,
            Height = height,
            Width = width,
            Size = size,
        };
        db.Set<VideoFile>().Add(file);
        await db.SaveChangesAsync(ct);

        return (folder.Id, video.Id, file.Id);
    }

    // Videos naming the same folder path share one Folder row. Seeded in a single save: a save per
    // row on one context rescans every entity the context already tracks, so a loop over
    // SeedVideoAsync costs the square of the row count.
    public static async Task<IReadOnlyList<int>> SeedVideosAsync(
        DbContext db, int count, Func<int, (string FolderPath, string Basename, string Title)> shape,
        bool organized = true, CancellationToken ct = default)
    {
        var folders = new Dictionary<string, Folder>(StringComparer.Ordinal);
        var videos = new List<Video>(count);
        for (int i = 0; i < count; i++)
        {
            var (folderPath, basename, title) = shape(i);
            string path = folderPath.Replace('\\', '/');
            if (!folders.TryGetValue(path, out var folder))
            {
                folder = new Folder { Path = path, ModTime = DateTime.UtcNow };
                folders.Add(path, folder);
                db.Set<Folder>().Add(folder);
            }

            var video = new Video { Title = title, Organized = organized };
            db.Set<Video>().Add(video);
            db.Set<VideoFile>().Add(new VideoFile
            {
                Basename = basename,
                ParentFolder = folder,
                Format = ExtOf(basename),
                Video = video,
            });
            videos.Add(video);
        }

        await db.SaveChangesAsync(ct);
        return [.. videos.Select(v => v.Id)];
    }

    public static async Task<(int folderId, int imageId, int fileId)> SeedImageAsync(
        DbContext db, string folderPath, string basename, string title,
        bool organized = true, CancellationToken ct = default)
    {
        var folder = new Folder { Path = folderPath.Replace('\\', '/'), ModTime = DateTime.UtcNow };
        db.Set<Folder>().Add(folder);
        await db.SaveChangesAsync(ct);

        var image = new Image { Title = title, Organized = organized };
        db.Set<Image>().Add(image);
        await db.SaveChangesAsync(ct);

        var file = new ImageFile
        {
            Basename = basename,
            ParentFolderId = folder.Id,
            Format = ExtOf(basename),
            ImageId = image.Id,
        };
        db.Set<ImageFile>().Add(file);
        await db.SaveChangesAsync(ct);

        return (folder.Id, image.Id, file.Id);
    }

    public static async Task<(int folderId, int audioId, int fileId)> SeedAudioAsync(
        DbContext db, string folderPath, string basename, string title,
        bool organized = true, CancellationToken ct = default)
    {
        var folder = new Folder { Path = folderPath.Replace('\\', '/'), ModTime = DateTime.UtcNow };
        db.Set<Folder>().Add(folder);
        await db.SaveChangesAsync(ct);

        var audio = new Audio { Title = title, Organized = organized };
        db.Set<Audio>().Add(audio);
        await db.SaveChangesAsync(ct);

        var file = new AudioFile
        {
            Basename = basename,
            ParentFolderId = folder.Id,
            Format = ExtOf(basename),
            AudioId = audio.Id,
        };
        db.Set<AudioFile>().Add(file);
        await db.SaveChangesAsync(ct);

        return (folder.Id, audio.Id, file.Id);
    }

    public static async Task<(int folderId, int textId, int fileId)> SeedTextAsync(
        DbContext db, string folderPath, string basename, string title,
        bool organized = true, CancellationToken ct = default)
    {
        var folder = new Folder { Path = folderPath.Replace('\\', '/'), ModTime = DateTime.UtcNow };
        db.Set<Folder>().Add(folder);
        await db.SaveChangesAsync(ct);

        var text = new TextDocument { Title = title, Organized = organized };
        db.Set<TextDocument>().Add(text);
        await db.SaveChangesAsync(ct);

        var file = new TextFile
        {
            Basename = basename,
            ParentFolderId = folder.Id,
            Format = ExtOf(basename),
            TextDocumentId = text.Id,
        };
        db.Set<TextFile>().Add(file);
        await db.SaveChangesAsync(ct);

        return (folder.Id, text.Id, file.Id);
    }

    // Seeds one entity of kind with one file, and returns the entity and file ids. The per-kind
    // seeders above differ in the entity type they create, so a case that is about a rule holding for
    // every kind reaches them through here rather than restating the switch.
    public static async Task<(int EntityId, int FileId)> SeedEntityAsync(
        DbContext db, RenamerFileKind kind, string folderPath, string basename, string? title,
        CancellationToken ct = default)
    {
        var (_, entityId, fileId) = kind switch
        {
            RenamerFileKind.Video => await SeedVideoAsync(db, folderPath, basename, title!, ct: ct),
            RenamerFileKind.Image => await SeedImageAsync(db, folderPath, basename, title!, ct: ct),
            RenamerFileKind.Audio => await SeedAudioAsync(db, folderPath, basename, title!, ct: ct),
            RenamerFileKind.Text => await SeedTextAsync(db, folderPath, basename, title!, ct: ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        return (entityId, fileId);
    }

    // One throwaway Video, so the next seeded entity's id runs ahead of its file's id. A case asserting
    // that a record carries an entity id cannot tell the two apart while they are equal.
    public static async Task SeedDecoyVideoAsync(DbContext db, CancellationToken ct = default)
    {
        db.Set<Video>().Add(new Video { Title = "decoy", Organized = true });
        await db.SaveChangesAsync(ct);
    }

    // A height of 0 renders no $resolution label.
    public static async Task<int> SeedAdditionalFileAsync(
        DbContext db, int folderId, int videoId, string basename, int height = 0, int width = 0,
        CancellationToken ct = default)
    {
        var file = new VideoFile
        {
            Basename = basename,
            ParentFolderId = folderId,
            Format = ExtOf(basename),
            VideoId = videoId,
            Height = height,
            Width = width,
        };
        db.Set<VideoFile>().Add(file);
        await db.SaveChangesAsync(ct);
        return file.Id;
    }

    // The tracker is cleared first because a failed save leaves the modified entity attached, so a
    // tracked read would report a title that never committed.
    public static async Task<string?> ReadVideoTitleAsync(DbContext db, int videoId, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        return await db.Set<Video>().AsNoTracking()
            .Where(v => v.Id == videoId).Select(v => v.Title).SingleAsync(ct);
    }

    public static async Task<(string basename, string path)> ReadFileAsync(DbContext db, int fileId, CancellationToken ct = default)
    {
        var f = await db.Set<BaseFileEntity>().AsNoTracking().FirstAsync(x => x.Id == fileId, ct);
        return (f.Basename, f.Path);
    }

    private static string ExtOf(string basename)
    {
        int dot = basename.LastIndexOf('.');
        return dot >= 0 && dot < basename.Length - 1 ? basename[(dot + 1)..] : "";
    }
}
