using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Renamer.Tests.Execution;

/// <summary>
/// Shared seeding helpers for the executor integration tier (Tasks 2 + 3). Seeds a Folder + Video +
/// VideoFile graph on a real <see cref="CoveContext"/> whose <c>Folder.Path</c> is the test's real
/// temp-directory root, so the planner's relative target + the DB Path-recompute + the on-disk move
/// all align on the same absolute location.
/// </summary>
internal static class ExecutorTestSeed
{
    /// <summary>
    /// Seeds a Folder (Path = <paramref name="folderPath"/>) + a Video titled <paramref name="title"/>
    /// + a single VideoFile (<paramref name="basename"/>). Returns the (folderId, videoId, fileId).
    /// </summary>
    public static async Task<(int folderId, int videoId, int fileId)> SeedVideoAsync(
        CoveContext db, string folderPath, string basename, string title,
        bool organized = true, CancellationToken ct = default)
    {
        var folder = new Folder { Path = folderPath.Replace('\\', '/'), ModTime = DateTime.UtcNow };
        db.Set<Folder>().Add(folder);
        await db.SaveChangesAsync(ct);

        var video = new Video { Title = title, Organized = organized };
        db.Set<Video>().Add(video);
        await db.SaveChangesAsync(ct);

        var file = new VideoFile
        {
            Basename = basename,
            ParentFolderId = folder.Id,
            Format = ExtOf(basename),
            VideoId = video.Id,
        };
        db.Set<VideoFile>().Add(file);
        await db.SaveChangesAsync(ct);

        return (folder.Id, video.Id, file.Id);
    }

    /// <summary>
    /// Seeds a Folder (Path = <paramref name="folderPath"/>) + an Image titled <paramref name="title"/>
    /// + a single ImageFile (<paramref name="basename"/>). Returns the (folderId, imageId, fileId).
    /// </summary>
    public static async Task<(int folderId, int imageId, int fileId)> SeedImageAsync(
        CoveContext db, string folderPath, string basename, string title,
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
        CoveContext db, string folderPath, string basename, string title,
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

    /// <summary>Adds another VideoFile in the same folder to an existing video (for collision/multi-file seeds).</summary>
    public static async Task<int> SeedAdditionalFileAsync(
        CoveContext db, int folderId, int videoId, string basename, CancellationToken ct = default)
    {
        var file = new VideoFile
        {
            Basename = basename,
            ParentFolderId = folderId,
            Format = ExtOf(basename),
            VideoId = videoId,
        };
        db.Set<VideoFile>().Add(file);
        await db.SaveChangesAsync(ct);
        return file.Id;
    }

    /// <summary>Reads back a file row's current (Basename, recomputed Path) from a fresh tracker read.</summary>
    public static async Task<(string basename, string path)> ReadFileAsync(CoveContext db, int fileId, CancellationToken ct = default)
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
