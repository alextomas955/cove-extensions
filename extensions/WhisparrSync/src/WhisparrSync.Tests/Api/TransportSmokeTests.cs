using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.Core.Auth;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// The only C# coverage of the REAL minimal-API transport boundary: the extension's MapEndpoints is
/// mounted into an in-process WebApplication/TestServer and driven over HTTP. Pins (1) the route table the
/// per-capability endpoint split must preserve — every mounted route resolves (never 404/405) —
/// and (2) that a representative response serializes over the wire and round-trips to its DTO through the
/// real JsonSerializerOptions. Thin by design: route-exists + shape only; the handler-level permission and
/// logic tests already exist. Cove-present tier (a CovePrincipal + the host request pipeline), so it is
/// Compile-Removed on the bare CI leg alongside the other endpoint tests.
/// </summary>
[Trait("Tier", "L2")]
public sealed class TransportSmokeTests
{
    private const string Base = "/api/extensions/com.alextomas955.whisparrsync";

    // Every route MapEndpoints registers (method + path). The route table must not silently drop one.
    public static TheoryData<string, string> Routes()
    {
        var data = new TheoryData<string, string>();
        foreach (var g in new[]
        {
            "/status", "/options", "/webhook-url", "/import-log",
            "/folder-overlap", "/scene-status-summary", "/entity-library-summary", "/sync-preview",
            "/file-settings",
        })
        {
            data.Add("GET", g);
        }

        foreach (var p in new[]
        {
            "/test-connection", "/options", "/register-webhook",
            "/webhook", "/monitor", "/monitor-status", "/scene-status-batch", "/entity-status-batch",
            "/scene-detail", "/scene-add", "/scene-search", "/scene-monitor", "/bulk-add-missing",
            "/bulk-search-monitored", "/reflect-owned", "/scene-exclusion", "/scene-grab-release",
            "/scene-releases-list", "/scene-search-upgrades", "/videos-batch", "/entities-batch",
            "/sync-library", "/file-settings",
        })
        {
            data.Add("POST", p);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Route_IsRegistered(string method, string path)
    {
        await using var host = await ExtensionRouteHost.BootAsync(CovePrincipal.Anonymous());

        using var req = new HttpRequestMessage(new HttpMethod(method), Base + path);
        if (method == "POST")
        {
            // A minimal JSON body so a required-body bind succeeds and the handler (its own permission
            // gate) runs, rather than 400ing at the bind before we ever reach our route's handler.
            req.Content = JsonContent.Create(new { });
        }

        var resp = await host.Client.SendAsync(req);

        Assert.NotEqual(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.NotEqual(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
    }

    [Fact]
    public async Task GetOptions_Authorized_RoundTripsToOptionsView()
    {
        var principal = new CovePrincipal
        {
            UserId = 1,
            Username = "test-user",
            Kind = PrincipalKind.User,
            Roles = new HashSet<string>(),
            Permissions = new HashSet<string> { Permissions.ExtensionsRead },
        };
        await using var host = await ExtensionRouteHost.BootAsync(principal);

        var resp = await host.Client.GetAsync(Base + "/options");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync();
        var view = JsonSerializer.Deserialize<OptionsView>(json);
        Assert.NotNull(view);
        // The redaction-safe projection carries the hasApiKey flag, not the raw key (OptionsView has no
        // key member, so a leak is structurally impossible — assert the flag round-trips through the wire).
        Assert.Contains("hasApiKey", json, StringComparison.Ordinal);
        Assert.False(view.HasApiKey);
    }
}
