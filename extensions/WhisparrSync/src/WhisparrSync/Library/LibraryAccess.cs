using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Options;
using static Cove.Extensions.Shared.RunAsSystem;
using static WhisparrSync.Contracts.WireSerializers;

namespace WhisparrSync;

/// <summary>
/// Scoped Cove-library access shared by every slice that reads the library: the per-request scoped-port
/// factory, the null-scope-safe whole-library / single-video loads, and the no-op port that satisfies
/// <c>SceneActions</c> for the paths that never enumerate. Each open opens a fresh <c>CreateAsyncScope()</c> so
/// the port's <c>DbContext</c> has the correct (never captured) lifetime.
/// </summary>
public sealed partial class WhisparrSync
{
    // Builds a scoped ICoveLibraryPort for the bulk-add-missing local enumeration and runs <paramref name="body"/>
    // inside the DB scope so the port's DbContext has the correct lifetime (never a long-lived captured context).
    // Degrades to an empty port when no host DB scope is available (mirrors LoadVideoByIdSafeAsync's null-scope
    // guard): with no scope the entity has no enumerable scenes, so add-all-missing finds nothing to register.
    private Task<IResult> WithScopedLibraryAsync(string stashEndpoint, string tpdbEndpoint, Func<ICoveLibraryPort, Task<IResult>> body)
        => WithScopedLibraryAsync<IResult>(stashEndpoint, tpdbEndpoint, body);

    // The generic form: runs <paramref name="body"/> inside the DB scope and returns its value (not only an
    // IResult), so a slice needing the COMPUTED data back — the discovery diff shares one catalogue-minus-owned
    // pass between the read handlers and the action endpoint — can run under the same scoped port. Same
    // null-scope / no-DbContext degradation as the IResult overload. When asSystem is set, the body runs under
    // CovePrincipal.System(): a library-wide read (the whole-owned subtraction the tag diff needs) is undercounted
    // under a non-owner principal by CoveContext's per-principal authz filters — the same reason the
    // performer-avatar read runs as System. The endpoint stays configure-gated on the caller; System governs only
    // the DB read filters.
    private async Task<T> WithScopedLibraryAsync<T>(
        string stashEndpoint, string tpdbEndpoint, Func<ICoveLibraryPort, Task<T>> body, bool asSystem = false)
    {
        if (_scopeFactory is null)
        {
            return await body(EmptyCoveLibraryPort.Instance);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<DbContext>() is not { } db)
        {
            return await body(EmptyCoveLibraryPort.Instance);
        }

        var port = new CoveLibraryPort(db, stashEndpoint, tpdbEndpoint);
        return asSystem
            ? await RunAsSystemAsync(scope.ServiceProvider, () => body(port))
            : await body(port);
    }

    // SceneActions requires an ICoveLibraryPort, but the per-scene + search operations (add/search/monitor/
    // search-all) never touch it — only AddAllMissing enumerates an entity's scenes. This no-op port satisfies
    // the constructor for those paths (the per-scene handlers resolve the scene via LoadVideoByIdSafeAsync, and
    // search-all needs no local enumeration); a scope-backed CoveLibraryPort is used only for add-all-missing.
    private sealed class EmptyCoveLibraryPort : ICoveLibraryPort
    {
        public static readonly EmptyCoveLibraryPort Instance = new();

        public async IAsyncEnumerable<CoveVideo> StreamAllVideosAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<int> CountVideosAsync(CancellationToken ct = default) => Task.FromResult(0);

        public async IAsyncEnumerable<int> StreamVideoIdsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<CoveVideo> StreamVideosInRangeAsync(
            int afterCoveId, int upToCoveId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<string> StreamFilePathsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlySet<string>> LoadOwnedRemoteIdsAsync(
            DiscoveryIdFamily family, IReadOnlyCollection<string> candidateIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public Task<CoveVideo?> LoadVideoByIdAsync(int coveId, CancellationToken ct = default)
            => Task.FromResult<CoveVideo?>(null);

        public Task<IReadOnlyList<CoveVideo>> LoadVideosByIdsAsync(
            IReadOnlyList<int> coveIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CoveVideo>>([]);

        public Task<IReadOnlyList<CoveVideo>> LoadVideosForEntityAsync(
            EntityKind kind, int coveEntityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CoveVideo>>([]);

        public Task<CoveEntityIdentity?> LoadEntityIdentityAsync(
            EntityKind kind, int coveEntityId, CancellationToken ct = default)
            => Task.FromResult<CoveEntityIdentity?>(null);

        public Task<IReadOnlyList<CoveEntityIdentity>> LoadStudioChildIdentitiesAsync(
            int coveEntityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CoveEntityIdentity>>([]);

        public Task<IReadOnlyList<CoveEntityIdentity>> LoadAllEntityIdentitiesAsync(
            EntityKind kind, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CoveEntityIdentity>>([]);

        public Task<IReadOnlyList<CoveEntityRef>> LoadAllEntityRefsAsync(
            EntityKind kind, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CoveEntityRef>>([]);

        public Task<IReadOnlyList<CovePerformerImage>> LoadPerformerImagesAsync(
            IReadOnlyCollection<string> stashIds, IReadOnlyCollection<string> names, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CovePerformerImage>>([]);
    }

    // Resolves one Cove video by id for the scene-detail/releases reads, degrading to null when no host DB scope
    // is available (the same defensive null-scope check WithScopedLibraryAsync makes). A null result is the
    // caller's NO_STASHDB_IDENTITY outcome — a not-resolvable scene never reaches Whisparr.
    private Task<CoveVideo?> LoadVideoByIdSafeAsync(int coveId, string stashEndpoint, string tpdbEndpoint, CancellationToken ct)
        => WithScopedLibraryAsync(stashEndpoint, tpdbEndpoint, port => port.LoadVideoByIdAsync(coveId, ct));

    // The per-scene routes' shared first step: resolve the scene the caller named and refuse it when there is no
    // usable identity to address Whisparr with. Exactly one of the two is non-null. The refusal is a 200 carrying
    // a discriminator, not an error — a scene Cove holds no remote id for is a state the panel renders, and it
    // must make no outbound call. Returned rather than thrown so the early return stays visible in the handler.
    private async Task<(CoveVideo? Video, IResult? Refusal)> ResolveSceneIdentityAsync(
        int coveId, WhisparrOptions options, CancellationToken ct)
    {
        var video = await LoadVideoByIdSafeAsync(coveId, options.StashDbEndpoint, options.TpdbEndpoint, ct);
        return video is null || video.StashIds.Count == 0
            ? (null, Results.Json(
                new NoIdentityResponse("NO_STASHDB_IDENTITY", ProviderNameFor(options)), EnumStringResponseJsonOptions))
            : (video, null);
    }

    // Resolves a studio's child sub-studios' identities under CovePrincipal.System() via the RunAsSystem seam: a
    // studio-hierarchy read spans the whole library and is undercounted under a non-owner principal by
    // CoveContext's per-principal authz filters (the same reason the performer-avatar read runs as System).
    // Degrades to an empty list when there is no host DB scope, so a missing scope leaves the studio single-id.
    private Task<IReadOnlyList<CoveEntityIdentity>> LoadStudioChildIdentitiesSafeAsync(
        int coveEntityId, string stashEndpoint, string tpdbEndpoint, CancellationToken ct)
        => WithScopedLibraryAsync(
            stashEndpoint, tpdbEndpoint,
            port => port.LoadStudioChildIdentitiesAsync(coveEntityId, ct),
            asSystem: true);
}
