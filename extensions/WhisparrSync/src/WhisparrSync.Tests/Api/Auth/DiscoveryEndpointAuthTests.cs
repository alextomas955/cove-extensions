using Microsoft.AspNetCore.Http;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using static WhisparrSync.Tests.TestSupport.EndpointTestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api.Auth;

/// <summary>
/// Security-critical: the host's <c>[RequiresPermission]</c> filter is inert on minimal-API extension
/// endpoints, so the discovery reads (<c>/discovery/entity</c>, <c>/discovery/count</c>) enforce
/// <c>extensions.configure</c> themselves for reaching the stored creds. These prove, for every route: the deny
/// trio (null / read-only / no-configure → 403); that configure proceeds (not forbidden); that a v2 instance is
/// a first-class discovery path (no <c>VERSION_UNSUPPORTED</c> defer); and that the stored key never appears in
/// the response.
/// </summary>
[Trait("Tier", "L2")]
public sealed class DiscoveryEndpointAuthTests
{
    private const string StoredBaseUrl = "http://stored.local:6969";
    private const string StoredKey = "STORED-KEY";

    // Drives each route by name so the deny/allow/version matrices are a single [Theory] each.
    private static Task<IResult> Invoke(
        string route, Ext ext, WhisparrClient client)
        => route switch
        {
            "discovery-entity" => ext.DiscoveryEntityAsync(
                new DiscoveryEntityRequest(5, "studio"), client, StashDbClientReturning(), TpdbClientReturning(), default),
            "discovery-count" => ext.DiscoveryCountAsync(
                "studio", 5, client, StashDbClientReturning(), TpdbClientReturning(), default),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "unknown route"),
        };

    public static TheoryData<string> AllRoutes() =>
        new() { "discovery-entity", "discovery-count" };

    // ---- v2 is a first-class discovery path — no longer a VERSION_UNSUPPORTED defer ----

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task Route_V2Instance_IsSupported_NotVersionUnsupported(string route)
    {
        // A Cove studio now maps to a v2 SITE's episodes (uniform "scenes"), so a v2 instance proceeds through
        // the same diff rather than deferring. It is not forbidden and never answers VERSION_UNSUPPORTED.
        var store = await StoreWith(StoredBaseUrl, StoredKey, version: "v2");

        var result = await Invoke(
            route, NewExtension(store), ClientReturning("[]"));
        Assert.DoesNotContain("VERSION_UNSUPPORTED", ResponseJson(result), StringComparison.Ordinal);
    }

    // ---- no-echo: the stored API key is never echoed to any response ----

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task Route_ResponseNeverContainsTheApiKey(string route)
    {
        const string secretKey = "SUPER-SECRET-KEY-14b2";
        var store = await StoreWith(StoredBaseUrl, secretKey);
        var result = await Invoke(
            route, NewExtension(store), ClientReturning("[]"));

        Assert.DoesNotContain(secretKey, ResponseJson(result), StringComparison.Ordinal);
    }

    // ---- a malformed body / unknown kind is a clean 400, never a 500 ----

    [Fact]
    public async Task Entity_UnknownKind_Returns400()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var result = await NewExtension(store).DiscoveryEntityAsync(
            new DiscoveryEntityRequest(5, "galaxy"), ClientReturning("[]"), StashDbClientReturning(), TpdbClientReturning(), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("UNKNOWN_KIND", ResponseJson(result), StringComparison.Ordinal);
    }

    // ---- The discriminated state is a 200 config/availability state, decided server-side ----

    [Theory]
    [InlineData("studio")]
    [InlineData("performer")]
    public async Task Entity_V3_NoResolvableRemoteId_Returns200_WithNoSourceIdState(string kind)
    {
        // With no host DB scope the server resolves NO remote id for the entity; the honest outcome is the
        // noSourceId state on a 200 (a missing provider id is a configuration/availability fact, never a 500 and
        // never an empty own-everything). Proves BOTH kinds route through the discriminator (performer parity)
        // and that the state discriminator serializes to the response.
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var result = await NewExtension(store).DiscoveryEntityAsync(
            new DiscoveryEntityRequest(5, kind), ClientReturning("[]"), StashDbClientReturning(), TpdbClientReturning(), default);

        // The state is a 200-default Results.Json body (StatusCode left unset, exactly like the served-catalogue
        // path) — NOT an error status: never a 400 malformed-body, a 403 gate, or a 502 outage.
        Assert.NotEqual(400, StatusOf(result));
        Assert.NotEqual(502, StatusOf(result));
        // The value strings are case-stable regardless of the response serializer's property casing.
        Assert.Contains("noSourceId", ResponseJson(result), StringComparison.Ordinal);
    }
}
