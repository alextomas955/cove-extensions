using Cove.Core.Auth;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using static Cove.Extensions.Shared.RunAsSystem;

namespace WhisparrSync.Tests.Batch;

/// <summary>
/// The RunAsSystem invariant guard (host-double). A background job scope carries no request principal, so its
/// ambient one is Anonymous — under which CoveContext's per-principal authz query filters undercount a library
/// read to ZERO rows with no error. That is the exact latent bug the two background batch jobs
/// (RunVideosBatchJob / RunEntitiesBatchJob) carried: each per-id/per-entity read came back null, so every unit
/// Skipped. Routing those reads through <c>RunAsSystemAsync</c> elevates to System for the read's span, which
/// bypasses the filters.
/// </summary>
/// <remarks>
/// The double reimplements CoveContext's authz gate exactly (bypass iff the principal is null, System, or holds
/// "*"). It is a double rather than a real CoveContext because the row-filtering is Npgsql-ONLY:
/// <c>CoveContext.OnModelCreating</c> wires the query filters + the <c>cove_authz_can_read</c> DB function
/// behind an <c>isNpgsql</c> gate, so a SQLite in-memory CoveContext installs NO filter and returns every row
/// regardless of principal — it cannot reproduce the undercount at the L1 tier. The end-to-end Postgres path is
/// covered by the cove-dev live-drive + the containerized e2e.
/// </remarks>
[Trait("Tier", "L1")]
public sealed class BackgroundScopeSystemTests
{
    private static readonly CoveVideo SeededScene = new(
        CoveId: 7, Title: "Scene A", Date: null, StashIds: ["uuid-a"], TpdbIds: [], FilePaths: [], Fingerprints: []);

    private static readonly CoveEntityRef SeededStudio = new(CoveId: 3, StashIds: ["studio-uuid"], TpdbIds: []);

    private static readonly CovePerformerImage SeededPerformer = new(
        CoveId: 42, Name: "Lilly Bell", StashIds: ["perf-uuid"]);

    [Fact]
    public async Task VideosBatchRead_UnderAnonymousBackgroundScope_UndercountsToZero()
    {
        var accessor = FakePrincipalAccessor.None(); // the background scope's ambient principal (never set)
        var port = new PrincipalGatedLibraryPort(accessor);

        // LoadVideoByIdAsync — the id resolution RunVideosBatchJob depends on — is null for every id under
        // Anonymous: the batch's all-Skipped undercount.
        Assert.Null(await port.LoadVideoByIdAsync(SeededScene.CoveId));
        Assert.Empty(await Materialize(port));
    }

    [Fact]
    public async Task VideosBatchRead_ThroughRunAsSystem_ResolvesTheFullLibrary()
    {
        var accessor = FakePrincipalAccessor.None();
        var port = new PrincipalGatedLibraryPort(accessor);
        var services = new PrincipalOnlyServices(accessor);

        var resolved = await RunAsSystemAsync(services, () => port.LoadVideoByIdAsync(SeededScene.CoveId));
        var all = await RunAsSystemAsync(services, () => Materialize(port));

        Assert.NotNull(resolved);
        Assert.Equal(SeededScene.CoveId, resolved!.CoveId);
        Assert.Single(all);

        // The prior principal is restored: a read outside the seam's span is undercounted again.
        Assert.Equal(PrincipalKind.Anonymous, accessor.Current!.Kind);
        Assert.Null(await port.LoadVideoByIdAsync(SeededScene.CoveId));
    }

    [Fact]
    public async Task EntitiesBatchRead_ZeroUnderAnonymous_FullUnderRunAsSystem()
    {
        var accessor = FakePrincipalAccessor.None();
        var port = new PrincipalGatedLibraryPort(accessor);
        var services = new PrincipalOnlyServices(accessor);

        // RunEntitiesBatchJob resolves each entity's identity via LoadEntityIdentityAsync; under Anonymous it is
        // null → Skipped, no outbound call.
        Assert.Null(await port.LoadEntityIdentityAsync(EntityKind.Studio, SeededStudio.CoveId));
        Assert.Empty(await port.LoadAllEntityRefsAsync(EntityKind.Studio));

        var identity = await RunAsSystemAsync(
            services, () => port.LoadEntityIdentityAsync(EntityKind.Studio, SeededStudio.CoveId));
        var refs = await RunAsSystemAsync(services, () => port.LoadAllEntityRefsAsync(EntityKind.Studio));

        Assert.NotNull(identity);
        Assert.Equal(["studio-uuid"], identity!.StashIds);
        Assert.Single(refs);
    }

    [Fact]
    public async Task PerformerImageRead_ZeroUnderAnonymous_ResolvedUnderRunAsSystem()
    {
        // The discovery card resolves a through-Whisparr performer's avatar from Cove's OWN performer records via
        // LoadPerformerImagesAsync; under an Anonymous scope the library-wide performer read undercounts to zero
        // (so every chip would fall back to a glyph), and only the RunAsSystem span sees the performer.
        var accessor = FakePrincipalAccessor.None();
        var port = new PrincipalGatedLibraryPort(accessor);
        var services = new PrincipalOnlyServices(accessor);

        Assert.Empty(await port.LoadPerformerImagesAsync(["perf-uuid"], []));

        var resolved = await RunAsSystemAsync(
            services, () => port.LoadPerformerImagesAsync(["perf-uuid"], []));

        var performer = Assert.Single(resolved);
        Assert.Equal(SeededPerformer.CoveId, performer.CoveId);
        Assert.Equal(["perf-uuid"], performer.StashIds);
    }

    [Fact]
    public async Task RunAsSystem_RestoresThePriorPrincipal_EvenWhenTheBodyThrows()
    {
        var accessor = FakePrincipalAccessor.None();
        var services = new PrincipalOnlyServices(accessor);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunAsSystemAsync(services, Task<int> () => throw new InvalidOperationException("boom")));

        Assert.Equal(PrincipalKind.Anonymous, accessor.Current!.Kind);
    }

    // A library-port double gated on the ambient principal with CoveContext's EXACT bypass predicate: a read
    // returns the seeded row iff the caller is authorized to bypass the authz filters (a null, System, or
    // "*"-holding principal). Every other principal — Anonymous here — reads nothing, mirroring the Postgres
    // per-principal query filter. This is the seam's whole point: the background read only sees the library once
    // elevated to System.
    private sealed class PrincipalGatedLibraryPort(ICurrentPrincipalAccessor accessor) : ICoveLibraryPort
    {
        private bool Bypassed =>
            accessor.Current is null
            || accessor.Current.Kind == PrincipalKind.System
            || accessor.Current.Has("*");

        // Gated identically: an undercounted count or boundary pass would plan the wrong ranges, and an
        // undercounted range read would silently visit nothing — the same Anonymous-principal failure.
        public Task<int> CountVideosAsync(CancellationToken ct = default)
            => Task.FromResult(Bypassed ? 1 : 0);

        public async IAsyncEnumerable<int> StreamVideoIdsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (Bypassed)
            {
                yield return SeededScene.CoveId;
            }

            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<CoveVideo> StreamVideosInRangeAsync(
            int afterCoveId, int upToCoveId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (Bypassed && SeededScene.CoveId > afterCoveId && SeededScene.CoveId <= upToCoveId)
            {
                yield return SeededScene;
            }

            await Task.CompletedTask;
        }

        // Gated identically to the materialized read — the library-wide folds read through this seam.
        public async IAsyncEnumerable<CoveVideo> StreamAllVideosAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (Bypassed)
            {
                yield return SeededScene;
            }

            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> StreamFilePathsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (Bypassed)
            {
                foreach (var path in SeededScene.FilePaths)
                {
                    yield return path;
                }
            }

            await Task.CompletedTask;
        }

        public Task<IReadOnlySet<string>> LoadOwnedRemoteIdsAsync(
            DiscoveryIdFamily family, IReadOnlyCollection<string> candidateIds, CancellationToken ct = default)
        {
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Bypassed)
            {
                foreach (var id in family == DiscoveryIdFamily.Tpdb ? SeededScene.TpdbIds : SeededScene.StashIds)
                {
                    if (candidateIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                    {
                        owned.Add(id);
                    }
                }
            }

            return Task.FromResult<IReadOnlySet<string>>(owned);
        }

        public Task<CoveVideo?> LoadVideoByIdAsync(int coveId, CancellationToken ct = default)
            => Task.FromResult(Bypassed && coveId == SeededScene.CoveId ? SeededScene : null);

        public Task<IReadOnlyList<CoveVideo>> LoadVideosByIdsAsync(
            IReadOnlyList<int> coveIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CoveVideo>>(
                Bypassed ? [.. new[] { SeededScene }.Where(v => coveIds.Contains(v.CoveId))] : []);

        public Task<IReadOnlyList<CoveVideo>> LoadVideosForEntityAsync(
            EntityKind kind, int coveEntityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CoveVideo>>(Bypassed ? [SeededScene] : []);

        public Task<CoveEntityIdentity?> LoadEntityIdentityAsync(
            EntityKind kind, int coveEntityId, CancellationToken ct = default)
            => Task.FromResult(Bypassed && coveEntityId == SeededStudio.CoveId
                ? new CoveEntityIdentity(SeededStudio.StashIds, SeededStudio.TpdbIds)
                : null);

        public Task<IReadOnlyList<CoveEntityIdentity>> LoadStudioChildIdentitiesAsync(
            int coveEntityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CoveEntityIdentity>>([]);

        public Task<IReadOnlyList<CoveEntityIdentity>> LoadAllEntityIdentitiesAsync(
            EntityKind kind, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CoveEntityIdentity>>(
                Bypassed ? [new CoveEntityIdentity(SeededStudio.StashIds, SeededStudio.TpdbIds)] : []);

        public Task<IReadOnlyList<CoveEntityRef>> LoadAllEntityRefsAsync(
            EntityKind kind, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CoveEntityRef>>(Bypassed ? [SeededStudio] : []);

        public Task<IReadOnlyList<CovePerformerImage>> LoadPerformerImagesAsync(
            IReadOnlyCollection<string> stashIds, IReadOnlyCollection<string> names, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CovePerformerImage>>(
                Bypassed && SeededPerformer.StashIds.Any(stashIds.Contains) ? [SeededPerformer] : []);
    }

    // The minimal scope-services the seam needs: it resolves only ICurrentPrincipalAccessor. Avoids pulling in a
    // full DI container for a one-service lookup.
    private sealed class PrincipalOnlyServices(ICurrentPrincipalAccessor accessor) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(ICurrentPrincipalAccessor) ? accessor : null;
    }

    // The library-wide read is a stream; the property under test is what it RETURNS under each principal, so
    // it is folded to a list here rather than asserted one page at a time.
    private static async Task<List<CoveVideo>> Materialize(ICoveLibraryPort port)
    {
        var videos = new List<CoveVideo>();
        await foreach (var video in port.StreamAllVideosAsync())
        {
            videos.Add(video);
        }

        return videos;
    }
}
