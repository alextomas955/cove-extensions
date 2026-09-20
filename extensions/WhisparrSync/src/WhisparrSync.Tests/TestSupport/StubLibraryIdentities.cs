using System.Runtime.CompilerServices;
using WhisparrSync.Contracts;
using WhisparrSync.Library;

namespace WhisparrSync.Tests.TestSupport;

// Enumerable more than once on purpose: a run walks a stream to count and again to offer, and a
// source that answered nothing the second time would report a library it never touched.
// A count this stub was not given throws rather than answering zero, so a run that reaches a member
// it has no business reaching fails instead of passing.
internal sealed class StubLibraryIdentities : ILibrarySceneIdentityPort
{
    private readonly IReadOnlyList<string> _scenes;
    private readonly IReadOnlyList<LibrarySiteIdentity> _sites;
    private readonly int? _unidentifiedScenes;
    private readonly int? _unidentifiedSites;

    private StubLibraryIdentities(
        IReadOnlyList<string> scenes,
        IReadOnlyList<LibrarySiteIdentity> sites,
        int? unidentifiedScenes,
        int? unidentifiedSites)
    {
        _scenes = scenes;
        _sites = sites;
        _unidentifiedScenes = unidentifiedScenes;
        _unidentifiedSites = unidentifiedSites;
    }

    public static StubLibraryIdentities OfScenes(
        IReadOnlyList<string> scenes, int? unidentified = null)
        => new(scenes, [], unidentified, null);

    public static StubLibraryIdentities OfSites(
        IReadOnlyList<LibrarySiteIdentity> sites, int? unidentified = null)
        => new([], sites, null, unidentified);

    public IAsyncEnumerable<string> SceneIdentities(
        WhisparrGeneration generation, CancellationToken ct)
        => Streamed(_scenes, ct);

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
