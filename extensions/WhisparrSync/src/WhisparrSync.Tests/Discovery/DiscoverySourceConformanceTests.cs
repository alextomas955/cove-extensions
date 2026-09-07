using System.Collections.Frozen;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Discovery;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Discovery;

/// <summary>
/// The cross-source guardrail: it enforces the discovery-source field contract over EVERY
/// <see cref="IDiscoverySource"/> implementation, not one source at a time. A source must either populate each
/// required <see cref="DiscoveryField"/> on the <see cref="WhisparrMovie"/> it maps for a representative scene, or
/// declare it in <see cref="IDiscoverySource.UnavailableFields"/>. A source that does neither — the way TPDB once
/// shipped thin cards — fails here, and a NEW source with no registered sample fails the reflected-vs-registered
/// equality check, keeping the gate exhaustive by mechanism.
/// </summary>
[Trait("Tier", "L0")]
public sealed class DiscoverySourceConformanceTests
{
    private static string StashDbFixture()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "TestSupport", "fixtures", "stashdb-queryScenes-studio.json"));

    private static string TpdbFixture()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "TestSupport", "fixtures", "tpdb-scenes-site.json"));

    private static string TpdbPerformerFixture()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "TestSupport", "fixtures", "tpdb-scenes-performer.json"));

    private static StashDbDiscoverySource StashDbSource(FakeHttpMessageHandler handler)
        => new(new StashDbGraphQlClient(new HttpClient(handler)), "https://stashdb.org/graphql", "test-key");

    private static StashDbDiscoverySource StashDbSource()
        => StashDbSource(FakeHttpMessageHandler.Json(StashDbFixture()));

    private static TpdbDiscoverySource TpdbSource(FakeHttpMessageHandler handler)
        => new(new TpdbClient(new HttpClient(handler)), "https://api.theporndb.net", "test-token");

    private static TpdbDiscoverySource TpdbSource(string fixture)
        => TpdbSource(FakeHttpMessageHandler.Json(fixture));

    // A source of the registered type, wired to a handler the caller can read the outbound request off.
    private static (IDiscoverySource Source, FakeHttpMessageHandler Handler) Probe(Type type)
    {
        var handler = FakeHttpMessageHandler.Json(
            type == typeof(TpdbDiscoverySource) ? TpdbFixture() : StashDbFixture());
        return (type == typeof(TpdbDiscoverySource) ? TpdbSource(handler) : StashDbSource(handler), handler);
    }

    private const string StashDbStudioId = "be4be46f-692f-4509-ba23-90a96abf0b16";
    private const string TpdbSiteId = "247";
    private const string TpdbPerformerId = "3de6cd92-0000-4000-8000-000000000001";
    private const string TpdbNumericTagId = "202";

    // One conformance sample per (source, KIND) — not merely per source: a source can serve several kinds through
    // different upstream criteria, and a kind whose payload is thinner than another's would slip past a
    // single-kind sample. Both surviving sources are direct (StashDB on v3, ThePornDB on v2), each carrying the
    // full rich field set on every kind it serves.
    private static IReadOnlyList<ConformanceSample> Samples()
        =>
        [
            new(typeof(StashDbDiscoverySource), StashDbSource(), EntityKind.Studio, [StashDbStudioId]),
            new(typeof(StashDbDiscoverySource), StashDbSource(), EntityKind.Performer, [StashDbStudioId]),
            new(typeof(StashDbDiscoverySource), StashDbSource(), EntityKind.Tag, [StashDbStudioId]),
            new(typeof(TpdbDiscoverySource), TpdbSource(TpdbFixture()), EntityKind.Studio, [TpdbSiteId]),
            new(
                typeof(TpdbDiscoverySource),
                TpdbSource(TpdbPerformerFixture()),
                EntityKind.Performer,
                [TpdbPerformerId]),
            // The tag-filtered read returns the same /scenes projection as the performer sub-resource (verified
            // live); only the upstream criterion differs, so one fixture covers both.
            new(
                typeof(TpdbDiscoverySource),
                TpdbSource(TpdbPerformerFixture()),
                EntityKind.Tag,
                [TpdbNumericTagId]),
        ];

    [Fact]
    public void Every_discovery_source_implementation_has_a_registered_sample()
    {
        // Reflection scans the production assembly — a new IDiscoverySource added there without a sample here
        // trips this equality, and the gate cannot be silently bypassed by shipping an unconformed source.
        var implementations = typeof(IDiscoverySource).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IDiscoverySource).IsAssignableFrom(t))
            .ToHashSet();
        var registered = Samples().Select(s => s.Type).ToHashSet();

        Assert.Equal(implementations, registered);
    }

    [Fact]
    public async Task Every_kind_a_source_actually_enumerates_has_a_registered_sample()
    {
        // Capability-by-presence, per kind: a kind is "served" when the source returns rows for it. No declaration
        // carries that set; probing it means a newly-opened kind with no sample trips this equality.
        var served = new HashSet<(Type, EntityKind)>();
        foreach (var probe in Samples().DistinctBy(s => s.Type))
        {
            foreach (var kind in Enum.GetValues<EntityKind>())
            {
                var source = probe.Type == typeof(TpdbDiscoverySource)
                    ? TpdbSource(TpdbFixture())
                    : (IDiscoverySource)StashDbSource();
                var result = await source.EnumerateCatalogueAsync(kind, probe.RemoteIds, default);
                if (result.IsOk && result.Value!.Movies.Length > 0)
                {
                    served.Add((probe.Type, kind));
                }
            }
        }

        var registered = Samples().Select(s => (s.Type, s.Kind)).ToHashSet();

        Assert.Equal(served, registered);
    }

    [Fact]
    public async Task Every_declared_server_side_sort_is_actually_applied_by_its_source()
    {
        // Capability-by-declaration only holds if a declaration is a fact. A source declaring an ordering it does
        // not put on the outbound request would have the tab claim the provider ordered the whole catalogue while
        // it silently ordered nothing — the exact lie the declaration exists to prevent. It reflects over the same
        // registered set as the other checks; a NEW source is then covered by mechanism.
        foreach (var probe in Samples().DistinctBy(s => s.Type))
        {
            var declared = Probe(probe.Type).Source.ServerSideSorts;
            var requestsPerMode = new List<string>();

            foreach (var mode in declared)
            {
                var (source, handler) = Probe(probe.Type);
                await source.EnumerateCataloguePageAsync(
                    probe.Kind, probe.RemoteIds, page: 1, perPage: 40,
                    DiscoveryQuery.Default with { Sort = mode }, default);

                // A REST provider carries the ordering on the url and a GraphQL one in the body; reading both
                // keeps this check provider-agnostic, which is what the spine itself is.
                var last = handler.Requests[^1];
                requestsPerMode.Add(last.Url + "\n" + last.Body);
            }

            Assert.Equal(
                declared.Count,
                requestsPerMode.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void An_undeclared_source_applies_nothing_server_side()
    {
        // The safe default, inverted from UnavailableFields on purpose: a source saying nothing about ordering and
        // facets is taken to do NEITHER. A new or unmodified source then degrades honestly; the opposite default
        // would hand it a capability it never had.
        IDiscoverySource stub = new StubSource(FrozenSet<DiscoveryField>.Empty);

        Assert.Empty(stub.ServerSideSorts);
        Assert.Empty(stub.ServerSideFacets);
        foreach (var kind in Enum.GetValues<EntityKind>())
        {
            Assert.Empty(stub.WholeSetFacetAxes(kind));
        }
    }

    [Fact]
    public async Task Every_source_populates_or_declares_every_required_field()
    {
        foreach (var sample in Samples())
        {
            var result = await sample.Source.EnumerateCatalogueAsync(sample.Kind, sample.RemoteIds, default);
            Assert.True(result.IsOk, $"{sample.Type.Name} enumeration was not Ok");

            // The richest scene stands in as the source's representative row — field order in the fixture must not
            // decide conformance.
            var representative = result.Value!.Movies
                .OrderByDescending(m => RequiredFields.Count(f => FieldPopulated[f](m)))
                .First();

            var (conforms, missing) = CheckFields(sample.Source, representative);
            Assert.True(conforms, $"{sample.Type.Name} neither populates nor declares {missing}");
        }
    }

    [Fact]
    public void An_undeclared_undermapped_field_fails_the_field_check()
    {
        // The gate's teeth: a source that leaves Overview null WITHOUT declaring it unavailable fails the check.
        // Driven through the single-source helper directly, not registered into the reflected-impl set.
        var underMapped = new WhisparrMovie(
            Id: 1, Title: "Scene", Year: null, StashId: "s1", ForeignId: null, ItemType: "scene",
            Monitored: false, HasFile: false, MovieFile: null, StudioTitle: "Studio", ReleaseDate: "2024-01-05",
            Images: [new WhisparrImage("screenshot", null, "https://cdn.example.test/cover.jpg")],
            PerformerNames: ["Performer One"], TagNames: ["Tag Alpha"], Overview: null,
            PerformerImageUrls: ["https://cdn.example.test/face/1.jpg"]);
        var stub = new StubSource(FrozenSet<DiscoveryField>.Empty);

        var (conforms, missing) = CheckFields(stub, underMapped);

        Assert.False(conforms);
        Assert.Equal(DiscoveryField.Overview, missing);
    }

    [Fact]
    public void A_declared_gap_exempts_a_field_from_the_check()
    {
        // The counterpart: the same undeclared under-map passes once the source declares Overview a real gap.
        var underMapped = new WhisparrMovie(
            Id: 1, Title: "Scene", Year: null, StashId: "s1", ForeignId: null, ItemType: "scene",
            Monitored: false, HasFile: false, MovieFile: null, StudioTitle: "Studio", ReleaseDate: "2024-01-05",
            Images: [new WhisparrImage("screenshot", null, "https://cdn.example.test/cover.jpg")],
            PerformerNames: ["Performer One"], TagNames: ["Tag Alpha"], Overview: null,
            PerformerImageUrls: ["https://cdn.example.test/face/1.jpg"]);
        var stub = new StubSource(FrozenSet.ToFrozenSet([DiscoveryField.Overview]));

        var (conforms, missing) = CheckFields(stub, underMapped);

        Assert.True(conforms);
        Assert.Null(missing);
    }

    private static readonly DiscoveryField[] RequiredFields = Enum.GetValues<DiscoveryField>();

    // How each governed field manifests on the mapped movie — the same derivation DiscoveryService.Project reads
    // (CoverUrl resolves through the landscape cover picker; PerformerImageUrl needs a non-empty avatar slot).
    private static readonly Dictionary<DiscoveryField, Func<WhisparrMovie, bool>> FieldPopulated =
        new()
        {
            [DiscoveryField.Title] = m => !string.IsNullOrEmpty(m.Title),
            [DiscoveryField.ReleaseDate] = m => !string.IsNullOrEmpty(m.ReleaseDate),
            [DiscoveryField.StudioName] = m => !string.IsNullOrEmpty(m.StudioTitle),
            [DiscoveryField.CoverUrl] = m => LandscapeCover(m) is not null,
            [DiscoveryField.PerformerName] = m => m.PerformerNames is { Length: > 0 },
            [DiscoveryField.PerformerImageUrl] = m =>
                m.PerformerImageUrls is { Length: > 0 } urls && urls.Any(u => !string.IsNullOrEmpty(u)),
            [DiscoveryField.Tags] = m => m.TagNames is { Length: > 0 },
            [DiscoveryField.Overview] = m => !string.IsNullOrEmpty(m.Overview),
        };

    private static (bool Conforms, DiscoveryField? Missing) CheckFields(IDiscoverySource source, WhisparrMovie movie)
    {
        foreach (var field in RequiredFields)
        {
            if (!FieldPopulated[field](movie) && !source.UnavailableFields.Contains(field))
            {
                return (false, field);
            }
        }

        return (true, null);
    }

    // Mirrors DiscoveryService's cover picker precedence (landscape kinds before the portrait poster).
    private static string? LandscapeCover(WhisparrMovie movie)
    {
        if (movie.Images is null)
        {
            return null;
        }

        foreach (var kind in new[] { "screenshot", "fanart", "banner", "poster" })
        {
            var image = movie.Images.FirstOrDefault(
                i => string.Equals(i.CoverType, kind, StringComparison.OrdinalIgnoreCase));
            var url = image is null ? null : (string.IsNullOrEmpty(image.RemoteUrl) ? image.Url : image.RemoteUrl);
            if (!string.IsNullOrEmpty(url))
            {
                return url;
            }
        }

        return null;
    }

    private sealed record ConformanceSample(
        Type Type, IDiscoverySource Source, EntityKind Kind, IReadOnlyList<string> RemoteIds);

    // A minimal source whose only interesting behavior is its declared gap set — the flip-test subject, defined in
    // the TEST assembly, invisible to the reflection scan of the production assembly.
    private sealed class StubSource(IReadOnlySet<DiscoveryField> declaredGaps) : IDiscoverySource
    {
        public IReadOnlySet<DiscoveryField> UnavailableFields => declaredGaps;

        public bool NeedsCredential => false;

        public bool CanEnumerateUnmonitored => false;

        public Task<WhisparrResult<DiscoveryCatalogue>> EnumerateCatalogueAsync(
            EntityKind kind, IReadOnlyList<string> remoteIds, CancellationToken ct)
            => Task.FromResult(WhisparrResult<DiscoveryCatalogue>.Ok(new DiscoveryCatalogue([], Truncated: false)));
    }
}
