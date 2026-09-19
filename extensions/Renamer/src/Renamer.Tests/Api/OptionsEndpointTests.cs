using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Cove.Plugins;
using Renamer.Contracts;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Api;

/// <summary>
/// The settings endpoints, driven over the real transport: <c>GET /options</c> and <c>PUT /options</c>.
/// </summary>
/// <remarks>
/// The round-trip test takes its request body from the server's own response rather than serializing one
/// the test composed. A body the test writes proves only that the test's serializer agrees with itself,
/// and the whole point of these endpoints is that the wire spelling and the stored spelling differ: the
/// wire is camelCase, the store holds the PascalCase spelling every installed blob already uses.
/// </remarks>
public sealed class OptionsEndpointTests
{
    private const string Route = TransportHost.BaseRoute + "/options";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // One settings document governs every kind, so these routes carry the permission Cove puts on its
    // own extension-data routes rather than a media permission. Holding write over one kind does not
    // reach it.
    private static FakePrincipalAccessor Configurer() =>
        FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure);

    private static async Task<IExtensionStore> StoreHolding(string? optionsJson)
    {
        var store = new FakeStore();
        if (optionsJson is not null)
        {
            await store.SetAsync(OptionsStore.Key, optionsJson);
        }

        return store;
    }

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Get_WithNothingSaved_AnswersTheDefaultsAndNoPendingWork()
    {
        await using var host = await TransportHost.BootAsync(Configurer());

        var resp = await host.Client.GetAsync(Route);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var view = await resp.Content.ReadFromJsonAsync<OptionsView>(Web);
        Assert.NotNull(view);
        Assert.Equal(new RenamerOptions(), view.Options);
        Assert.False(view.PendingNameMigration);
        Assert.False(view.PendingDestinationMigration);
        Assert.False(view.Unreadable);
    }

    [Fact]
    public async Task Put_StoresTheEditInThePersistedSpelling_AndGetReadsItBack()
    {
        var store = await StoreHolding(null);
        await using var host = await TransportHost.BootAsync(Configurer(), store);

        var loaded = JsonNode.Parse(await host.Client.GetStringAsync(Route))!["options"]!;
        loaded["filenameTemplate"] = "$title";

        var put = await host.Client.PutAsync(Route, JsonBody(loaded.ToJsonString()));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        // The stored blob, not the response: the spelling on disk is what every installed blob already
        // uses and what the extension's own load path reads.
        var stored = JsonNode.Parse((await store.GetAsync(OptionsStore.Key))!)!;
        Assert.Equal("$title", (string?)stored["FilenameTemplate"]);
        Assert.Equal("DropAll", (string?)stored["Performers"]!["OnOverflow"]);

        var view = await host.Client.GetFromJsonAsync<OptionsView>(Route, Web);
        Assert.NotNull(view);
        Assert.Equal("$title", view.Options.FilenameTemplate);
        Assert.Equal(OverflowPolicy.DropAll, view.Options.Performers.OnOverflow);
    }

    // The panel's type drops the generated optional markers, so every member has to be on the wire. A
    // member the server omitted would read as undefined behind a type that says it cannot be.
    [Fact]
    public async Task Get_WritesEveryMemberTheModelDeclares()
    {
        await using var host = await TransportHost.BootAsync(Configurer());

        var options = JsonNode.Parse(await host.Client.GetStringAsync(Route))!["options"]!.AsObject();
        var declared = typeof(RenamerOptions)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Length;

        Assert.Equal(declared, options.Count);
    }

    // The per-kind map is keyed by an enum, so its key crosses the same spelling boundary the property
    // names do, and the panel indexes that map by a key it has to spell correctly.
    [Fact]
    public async Task Get_SpellsThePerKindMapKeyForTheWire_AndPutStoresItBack()
    {
        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(new RenamerOptions
        {
            Kinds = new Dictionary<RenamerFileKind, KindOptions> { [RenamerFileKind.Video] = new() { Enabled = false } },
        });
        await using var host = await TransportHost.BootAsync(Configurer(), store);

        var loaded = JsonNode.Parse(await host.Client.GetStringAsync(Route))!["options"]!;
        Assert.Equal(["video"], loaded["kinds"]!.AsObject().Select(entry => entry.Key));

        var put = await host.Client.PutAsync(Route, JsonBody(loaded.ToJsonString()));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var stored = JsonNode.Parse((await store.GetAsync(OptionsStore.Key))!)!;
        Assert.Equal(["Video"], stored["Kinds"]!.AsObject().Select(entry => entry.Key));
        Assert.False((bool?)stored["Kinds"]!["Video"]!["Enabled"]);
    }

    [Fact]
    public async Task Put_RefusesWhileNameKeyedRulesStillAwaitConversion()
    {
        const string legacy = """{"TagDestinations":{"anime":{"Root":"","Template":"x"}}}""";
        var store = await StoreHolding(legacy);
        await using var host = await TransportHost.BootAsync(Configurer(), store);

        var view = await host.Client.GetFromJsonAsync<OptionsView>(Route, Web);
        Assert.NotNull(view);
        Assert.True(view.PendingNameMigration);

        var put = await host.Client.PutAsync(Route, JsonBody("""{"FilenameTemplate":"$title"}"""));
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);

        var code = await put.Content.ReadFromJsonAsync<ErrorCode>(Web);
        Assert.Equal("MIGRATION_PENDING", code!.Code);
        Assert.Equal(legacy, await store.GetAsync(OptionsStore.Key));
    }

    [Fact]
    public async Task Put_RefusesWhileADestinationIsStillABarePath()
    {
        const string legacy = """{"TagDestinations":{"7":"I:/library/anime"}}""";
        var store = await StoreHolding(legacy);
        await using var host = await TransportHost.BootAsync(Configurer(), store);

        var view = await host.Client.GetFromJsonAsync<OptionsView>(Route, Web);
        Assert.NotNull(view);
        Assert.True(view.PendingDestinationMigration);

        var put = await host.Client.PutAsync(Route, JsonBody("""{"FilenameTemplate":"$title"}"""));
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Equal(legacy, await store.GetAsync(OptionsStore.Key));
    }

    [Fact]
    public async Task Get_WithABlobThatCannotBeParsed_AnswersDefaultsAndSaysSo()
    {
        await using var host = await TransportHost.BootAsync(Configurer(), await StoreHolding("{not json"));

        var view = await host.Client.GetFromJsonAsync<OptionsView>(Route, Web);
        Assert.NotNull(view);
        Assert.True(view.Unreadable);
        Assert.Equal(new RenamerOptions(), view.Options);
    }

    [Fact]
    public async Task Put_WithABodyThatCannotBeParsed_IsRejectedAndWritesNothing()
    {
        var store = new FakeStore();
        await using var host = await TransportHost.BootAsync(Configurer(), store);

        var put = await host.Client.PutAsync(Route, JsonBody("{not json"));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(0, store.SetCallCount);
    }

    [Fact]
    public async Task Get_Anonymous_IsForbidden()
    {
        await using var host = await TransportHost.BootAsync(FakePrincipalAccessor.None());
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync(Route)).StatusCode);
    }

    // A caller who may rename videos may not reconfigure the extension: one settings document decides
    // how every kind is named and where it is moved, and the auto-rename it can switch on runs later as
    // System.
    [Fact]
    public async Task Put_WithMediaWriteButNotExtensionsConfigure_IsForbiddenAndWritesNothing()
    {
        var store = new FakeStore();
        await using var host = await TransportHost.BootAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead, Permissions.VideosWrite),
            store);

        var put = await host.Client.PutAsync(Route, JsonBody("""{"FilenameTemplate":"$title"}"""));

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(0, store.SetCallCount);
    }

    [Fact]
    public async Task Get_WithMediaReadButNotExtensionsConfigure_IsForbidden()
    {
        await using var host = await TransportHost.BootAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead));

        var resp = await host.Client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
