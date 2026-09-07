using System.Collections.Frozen;
using System.Reflection;
using WhisparrSync.Contracts;
using WhisparrSync.Discovery;

namespace WhisparrSync.Tests.Discovery;

/// <summary>
/// The discovery query coordinate, in one file because it is one contract: what the guard will accept from a
/// browser, what the cache key must separate, and the rule that the read request and BOTH action requests expose
/// the same query surface. A dimension can be lost from any one of the three and stay invisible everywhere else,
/// which is what these cases exist to stop.
/// </summary>
[Trait("Tier", "L0")]
public sealed class DiscoveryQueryGuardTests
{
    private const string WellFormedStashDbId = "be4be46f-692f-4509-ba23-90a96abf0b16";
    private const string WellFormedTpdbId = "289008";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("trending")]
    [InlineData("Title")]
    [InlineData("date_desc")]
    public void An_unrecognized_or_absent_sort_falls_back_to_the_shipped_ordering(string? sort)
    {
        // A hand-edited url must yield the default view, never a 400 and never a provider value nobody validated.
        // "Title" is here on purpose: the wire vocabulary is camelCase, and a near-miss is still a miss.
        var query = DiscoveryQueryGuard.Normalize(new DiscoveryQueryRequest(Sort: sort), isV2: false);

        Assert.Equal(DiscoverySortMode.Newest, query.Sort);
    }

    [Theory]
    [InlineData(DiscoverySortModes.Newest)]
    [InlineData(DiscoverySortModes.Oldest)]
    [InlineData(DiscoverySortModes.Title)]
    public void Each_known_sort_literal_round_trips_through_the_enum(string literal)
    {
        // Both directions of the one vocabulary mapping: the literal a client sends becomes a mode, and the mode
        // a source declares projects back to the same literal. A pair that failed to round-trip would leave the
        // control offering an ordering the response then reports as unsupported.
        var mode = DiscoveryQueryGuard.Normalize(new DiscoveryQueryRequest(Sort: literal), isV2: false).Sort;

        Assert.Equal([literal], DiscoveryQueryGuard.SortWireNames(FrozenSet.ToFrozenSet([mode])));
    }

    [Fact]
    public void The_three_sort_literals_map_to_three_distinct_modes()
    {
        // The round-trip above holds pairwise; this is what stops two literals collapsing onto one mode.
        var modes = new[] { DiscoverySortModes.Newest, DiscoverySortModes.Oldest, DiscoverySortModes.Title }
            .Select(literal => DiscoveryQueryGuard.Normalize(new DiscoveryQueryRequest(Sort: literal), isV2: false).Sort)
            .ToHashSet();

        Assert.Equal(3, modes.Count);
    }

    [Fact]
    public void An_absent_query_normalizes_to_the_shipped_read()
    {
        Assert.Equal(DiscoveryQuery.Default, DiscoveryQueryGuard.Normalize(null, isV2: false));
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("289008")]
    [InlineData("be4be46f692f4509ba2390a96abf0b16")]
    [InlineData("be4be46f-692f-4509-ba23-90a96abf0b16-extra")]
    [InlineData("'; DROP TABLE scenes; --")]
    public void A_stashdb_filter_id_of_the_wrong_shape_is_dropped_and_the_read_still_yields_a_query(string id)
    {
        var query = DiscoveryQueryGuard.Normalize(
            new DiscoveryQueryRequest(StudioId: id, PerformerId: id, TagId: id), isV2: false);

        // The AXIS degrades; the request does not fail. A guard that threw would turn a stale bookmark into an
        // error page, and one that passed the value through would put an unvalidated string on a provider query.
        Assert.Null(query.StudioFilterId);
        Assert.Null(query.PerformerFilterId);
        Assert.Null(query.TagFilterId);
        Assert.Equal(DiscoverySortMode.Newest, query.Sort);
    }

    [Fact]
    public void A_well_formed_stashdb_filter_id_survives_on_every_axis()
    {
        var query = DiscoveryQueryGuard.Normalize(
            new DiscoveryQueryRequest(
                StudioId: WellFormedStashDbId, PerformerId: WellFormedStashDbId, TagId: WellFormedStashDbId),
            isV2: false);

        Assert.Equal(WellFormedStashDbId, query.StudioFilterId);
        Assert.Equal(WellFormedStashDbId, query.PerformerFilterId);
        Assert.Equal(WellFormedStashDbId, query.TagFilterId);
    }

    [Theory]
    [InlineData("28h008")]
    [InlineData("289008a")]
    [InlineData(" 289008 x")]
    [InlineData("-289008")]
    [InlineData("2890082890082")]
    [InlineData(WellFormedStashDbId)]
    public void A_theporndb_filter_id_carrying_anything_but_digits_is_dropped(string id)
    {
        // ThePornDB answers a malformed parameter with a 200 and ignores it; the provider will never report one.
        // Digits-only at the edge is what keeps a crafted value out of the url fragment it is escaped into.
        var query = DiscoveryQueryGuard.Normalize(
            new DiscoveryQueryRequest(StudioId: id, PerformerId: id, TagId: id), isV2: true);

        Assert.Null(query.StudioFilterId);
        Assert.Null(query.PerformerFilterId);
        Assert.Null(query.TagFilterId);
    }

    [Fact]
    public void A_digit_string_survives_as_a_theporndb_filter_id()
    {
        var query = DiscoveryQueryGuard.Normalize(
            new DiscoveryQueryRequest(StudioId: WellFormedTpdbId, PerformerId: WellFormedTpdbId, TagId: WellFormedTpdbId),
            isV2: true);

        Assert.Equal(WellFormedTpdbId, query.StudioFilterId);
        Assert.Equal(WellFormedTpdbId, query.PerformerFilterId);
        Assert.Equal(WellFormedTpdbId, query.TagFilterId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2021)]
    [InlineData(1879)]
    [InlineData(2201)]
    [InlineData(int.MaxValue)]
    public void A_year_outside_the_clamp_is_dropped_and_the_read_still_yields_a_query(int year)
    {
        var query = DiscoveryQueryGuard.Normalize(new DiscoveryQueryRequest(Year: year), isV2: false);

        Assert.Null(query.Year);
        Assert.Equal(DiscoverySortMode.Newest, query.Sort);
    }

    [Theory]
    [InlineData(1880)]
    [InlineData(2021)]
    [InlineData(2200)]
    public void A_year_inside_the_clamp_survives(int year)
    {
        Assert.Equal(year, DiscoveryQueryGuard.Normalize(new DiscoveryQueryRequest(Year: year), isV2: false).Year);
    }

    // One differing pair PER query dimension. A sort-only assertion would leave four silent ways to serve a
    // cached page built for a different query, which is the whole failure mode the key exists to prevent.
    public static TheoryData<string> QueryDimensions()
    {
        var data = new TheoryData<string>();
        foreach (var dimension in CoveredDimensions)
        {
            data.Add(dimension);
        }

        return data;
    }

    private static readonly string[] CoveredDimensions =
        ["Sort", "StudioFilterId", "PerformerFilterId", "TagFilterId", "Year"];

    // The one-dimension-off query for a named dimension. Every value differs from DiscoveryQuery.Default's.
    private static DiscoveryQuery Varied(string dimension)
        => dimension switch
        {
            "Sort" => DiscoveryQuery.Default with { Sort = DiscoverySortMode.Title },
            "StudioFilterId" => DiscoveryQuery.Default with { StudioFilterId = WellFormedStashDbId },
            "PerformerFilterId" => DiscoveryQuery.Default with { PerformerFilterId = WellFormedStashDbId },
            "TagFilterId" => DiscoveryQuery.Default with { TagFilterId = WellFormedStashDbId },
            "Year" => DiscoveryQuery.Default with { Year = 2021 },
            _ => throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "no variation defined"),
        };

    [Theory]
    [MemberData(nameof(QueryDimensions))]
    public void The_cache_key_separates_two_queries_differing_in_any_single_dimension(string dimension)
    {
        Assert.NotEqual(Key(DiscoveryQuery.Default, page: 1), Key(Varied(dimension), page: 1));
    }

    [Fact]
    public void The_dimension_table_covers_every_member_of_the_query()
    {
        // The table is the coverage claim; this is the claim that the table did not shrink. A dimension added to
        // DiscoveryQuery with no row above would be left out of the key with every other case still green.
        var members = typeof(DiscoveryQuery).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => name != "EqualityContract")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(members, CoveredDimensions.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void The_cache_key_is_stable_for_equal_queries_and_still_separates_two_pages()
    {
        var query = DiscoveryQuery.Default with { Sort = DiscoverySortMode.Oldest, Year = 2021 };
        var same = DiscoveryQuery.Default with { Sort = DiscoverySortMode.Oldest, Year = 2021 };

        Assert.Equal(Key(query, page: 1), Key(same, page: 1));
        Assert.NotEqual(Key(query, page: 1), Key(query, page: 2));
        Assert.NotEqual(Key(query, page: 1), Key(query, page: null));
    }

    private static string Key(DiscoveryQuery query, int? page)
        => DiscoveryCacheKeys.For("v3", "https://box", "key", EntityKind.Studio, ["stash-1"], page, query);

    // The operands each action route owns. They are NOT query dimensions: an op selector, the id being acted on
    // and a selection subset name WHAT to do, while everything else names WHICH SET the server re-derives. Adding
    // a name here is the deliberate act that admits a new operand; adding a query dimension is not.
    private static readonly Dictionary<Type, string[]> ActionOperands = new()
    {
        [typeof(DiscoveryEntityRequest)] = Array.Empty<string>(),
        [typeof(DiscoveryActionRequest)] = ["SourceId", "Op"],
        [typeof(DiscoveryActionAllRequest)] = ["Op", "SourceIds"],
    };

    [Fact]
    public void The_read_request_and_both_action_requests_expose_the_same_query_surface()
    {
        var surfaces = ActionOperands.ToDictionary(
            entry => entry.Key,
            entry => entry.Key.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .Where(name => name != "EqualityContract" && !entry.Value.Contains(name, StringComparer.Ordinal))
                .ToHashSet(StringComparer.Ordinal));

        var reference = surfaces[typeof(DiscoveryEntityRequest)];
        foreach (var (type, surface) in surfaces)
        {
            Assert.True(
                reference.SetEquals(surface),
                $"{type.Name} does not expose the same query surface as the read request. Two halves of one rule: "
                + "a query dimension lands on the read request AND both action requests in the same change, and a "
                + "dimension added BESIDE the shared record (a per-page, a cursor, a scope selector) is a dimension "
                + $"too. Read: [{string.Join(", ", reference.Order(StringComparer.Ordinal))}]. "
                + $"{type.Name}: [{string.Join(", ", surface.Order(StringComparer.Ordinal))}].");
        }
    }

    [Fact]
    public void All_three_requests_carry_the_query_as_the_same_shared_record()
    {
        // Set equality over NAMES could be satisfied by a coincidence of naming. The TYPE is what makes the three
        // members one shared record; three same-named fields of different types would pass the surface check.
        foreach (var type in ActionOperands.Keys)
        {
            var property = type.GetProperty("Query", BindingFlags.Public | BindingFlags.Instance);
            Assert.True(property is not null, $"{type.Name} carries no Query member");
            Assert.Equal(typeof(DiscoveryQueryRequest), property!.PropertyType);
        }
    }
}
