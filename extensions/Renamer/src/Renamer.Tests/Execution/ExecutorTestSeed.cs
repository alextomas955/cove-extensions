using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Renamer.Tests.Execution;

/// <summary>
/// Shared seeding helpers for the executor integration tier. Seeds a Folder + Video +
/// VideoFile graph on a real <see cref="CoveContext"/> whose <c>Folder.Path</c> is the test's real
/// temp-directory root, so the planner's relative target + the DB Path-recompute + the on-disk move
/// all align on the same absolute location.
/// </summary>
internal static class ExecutorTestSeed
{
    /// <summary>
    /// Seeds a Folder (Path = <paramref name="folderPath"/>) + a Video titled <paramref name="title"/>
    /// + a single VideoFile (<paramref name="basename"/>). Returns the (folderId, videoId, fileId).
    /// A null <paramref name="date"/> leaves <c>$date</c>/<c>$year</c> absent. The
    /// <c>$resolution</c> label needs both dimensions, so leaving either
    /// <paramref name="height"/> or <paramref name="width"/> at 0 renders none.
    /// </summary>
    public static async Task<(int folderId, int videoId, int fileId)> SeedVideoAsync(
        DbContext db, string folderPath, string basename, string title,
        bool organized = true, DateOnly? date = null, int height = 0, int width = 0,
        CancellationToken ct = default)
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
        };
        db.Set<VideoFile>().Add(file);
        await db.SaveChangesAsync(ct);

        return (folder.Id, video.Id, file.Id);
    }

    /// <summary>
    /// Seeds <paramref name="count"/> videos with one VideoFile each, in a single save, and returns
    /// the video ids in seed order. <paramref name="shape"/> names each one's folder, basename and
    /// title; videos naming the same folder path share one Folder row.
    /// </summary>
    /// <remarks>
    /// A save per row on one context rescans every entity the context already tracks, so a loop over
    /// <see cref="SeedVideoAsync"/> costs the square of the row count.
    /// </remarks>
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

    /// <summary>
    /// Seeds a Folder (Path = <paramref name="folderPath"/>) + an Image titled <paramref name="title"/>
    /// + a single ImageFile (<paramref name="basename"/>). Returns the (folderId, imageId, fileId).
    /// </summary>
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

    /// <summary>
    /// Seeds a Folder (Path = <paramref name="folderPath"/>) + an Audio titled <paramref name="title"/>
    /// + a single AudioFile (<paramref name="basename"/>). Returns the (folderId, audioId, fileId).
    /// </summary>
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

    /// <summary>
    /// Seeds a Folder (Path = <paramref name="folderPath"/>) + a TextDocument titled
    /// <paramref name="title"/> + a single TextFile (<paramref name="basename"/>). Returns the
    /// (folderId, textDocumentId, fileId).
    /// </summary>
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

    /// <summary>
    /// Adds another VideoFile in the same folder to an existing video (for collision/multi-file seeds).
    /// A <paramref name="height"/> of 0 renders no <c>$resolution</c> label.
    /// </summary>
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

    /// <summary>Reads a Video's stored title from the row, discarding whatever the tracker still holds.</summary>
    /// <remarks>
    /// The tracker is cleared first because a failed save leaves the modified entity attached, so a
    /// tracked read would report a title that never committed.
    /// </remarks>
    public static async Task<string?> ReadVideoTitleAsync(DbContext db, int videoId, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        return await db.Set<Video>().AsNoTracking()
            .Where(v => v.Id == videoId).Select(v => v.Title).SingleAsync(ct);
    }

    /// <summary>Reads back a file row's current (Basename, recomputed Path) from a fresh tracker read.</summary>
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
