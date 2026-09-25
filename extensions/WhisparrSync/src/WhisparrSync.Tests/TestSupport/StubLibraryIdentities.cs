using System.Runtime.CompilerServices;
using WhisparrSync.Contracts;
using WhisparrSync.Library;

namespace WhisparrSync.Tests.TestSupport;

// Enumerable more than once on purpose: a run walks a stream to count and again to offer, and a
// source answering nothing the second time would report a library it never touched. A count this
// stub was not given throws rather than answering zero.
internal sealed class StubLibraryIdentities : ILibrarySceneIdentityPort
{
    private readonly IReadOnlyList<string> _scenes;
    private readonly IReadOnlyList<LibrarySceneInFolder> _byFolder;
    private readonly IReadOnlyList<LibrarySiteIdentity> _sites;
    private readonly int? _unidentifiedScenes;
    private readonly int? _unidentifiedSites;

    private StubLibraryIdentities(
        IReadOnlyList<string> scenes,
        IReadOnlyList<LibrarySiteIdentity> sites,
        int? unidentifiedScenes,
        int? unidentifiedSites,
        IReadOnlyList<LibrarySceneInFolder>? byFolder = null)
    {
        _scenes = scenes;

        // One folder holding every scene, unless a case states the folders itself. The scene pass
        // walks the folder-carrying member, so a stub answering nothing there would report a
        // library that holds no scene at all.
        _byFolder = byFolder
            ?? [.. scenes.Select(scene => new LibrarySceneInFolder(OneFolder, scene))];
        _sites = sites;
        _unidentifiedScenes = unidentifiedScenes;
        _unidentifiedSites = unidentifiedSites;
    }

    /// <summary>The folder every scene sits in where a case does not say otherwise.</summary>
    internal const string OneFolder = "/library/one";

    public static StubLibraryIdentities OfScenes(
        IReadOnlyList<string> scenes, int? unidentified = null)
        => new(scenes, [], unidentified, null);

    // For a case whose subject is the folder walk: which folder each scene is carried under, and
    // which folders carry none.
    public static StubLibraryIdentities OfScenesInFolders(
        IReadOnlyList<LibrarySceneInFolder> byFolder, int? unidentified = null)
        => new(
            [.. byFolder.Select(row => row.RemoteId).OfType<string>()],
            [],
            unidentified,
            null,
            byFolder);

    public static StubLibraryIdentities OfSites(
        IReadOnlyList<LibrarySiteIdentity> sites, int? unidentified = null)
        => new([], sites, null, unidentified);

    public IAsyncEnumerable<string> SceneIdentities(
        WhisparrGeneration generation, CancellationToken ct)
        => Streamed(_scenes, ct);

    // Every scene this stub holds, under the one folder it puts them in, named for its identifier so
    // a case can pair a listing row against it.
    public IAsyncEnumerable<LibraryFileIdentity> FileIdentitiesIn(
        string coveFolder, WhisparrGeneration generation, CancellationToken ct)
        => Streamed(
            [.. _byFolder
                .Where(row => row.RemoteId is not null && row.Folder == coveFolder)
                .Select(row => new LibraryFileIdentity(row.RemoteId! + ".mp4", row.RemoteId!))],
            ct);

    public IAsyncEnumerable<LibrarySceneInFolder> SceneIdentitiesByFolder(
        WhisparrGeneration generation, IReadOnlyList<string> rootOrder, CancellationToken ct)
        => Streamed(_byFolder, ct);

    public IAsyncEnumerable<LibrarySiteIdentity> SiteIdentities(
        WhisparrGeneration generation, CancellationToken ct)
        => Streamed(_sites, ct);

    public Task<int> CountUnidentifiedAsync(WhisparrGeneration generation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Answer(_unidentifiedScenes, "scene");
    }

    public Task<int> CountUnidentifiedSitesAsync(
        WhisparrGeneration generation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Answer(_unidentifiedSites, "site");
    }

    private static Task<int> Answer(int? given, string kind)
        => given is { } count
            ? Task.FromResult(count)
            : throw new InvalidOperationException(
                $"This library was given no unidentified {kind} count, so nothing under test should "
                    + "be reading one.");

    private static async IAsyncEnumerable<T> Streamed<T>(
        IEnumerable<T> identities, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var identity in identities)
        {
            ct.ThrowIfCancellationRequested();
            yield return identity;
        }

        await Task.CompletedTask;
    }
}
