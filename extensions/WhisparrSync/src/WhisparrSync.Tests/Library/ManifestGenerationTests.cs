using System.Globalization;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Library;

// A slot nothing registers makes the host render no wrapper element, so an omission here removes
// the surface from the page. All three registration groups are projected, because a registration
// written outside the generation-conditional block reaches both manifests.
public sealed class ManifestGenerationTests
{
    // The surfaces v2 has no meaning for. It reaches no scene without the site holding it, and it
    // holds no performer entity, so a control drawn here could only refuse.
    private static readonly string[] SlotsAbsentOnV2 =
    [
        "videos-list-toolbar-end",
        "video-card-content",
        "videos-list-row",
        "performer-detail-actions",
        "performers-list-toolbar-end",
        "performer-card-footer",
        "performers-list-row",
    ];

    // The whole tuple, so a count route or a glyph added to the registration is reported here. The
    // video detail page keeps only a contributed tab's key, label and manual contexts, so either
    // would be fetched and drawn by nothing.
    private static readonly string[] TabsAbsentOnV2 =
    [
        "performer|whisparr-missing|Missing|WhisparrMissingTab|150|"
            + "/api/extensions/com.alextomas955.whisparrsync/entity/performer/{entityId}/missing/count"
            + "|no glyph",
        "video|whisparr-scene|Whisparr|WhisparrSceneTab|150|no count route|no glyph",
    ];

    // The entity type is the spelling the host's selection bar passes: singular for a video
    // selection, plural for a studio or performer one. The wrong number makes the button not
    // appear, with no error anywhere.
    private static readonly string[] ActionsAbsentOnV2 =
    [
        "whisparr-monitor-selected-performers|bulk|performers|whisparrMonitorSelected|no endpoint|100",
        "whisparr-scene-batch|bulk|video|whisparrSceneBatch|no endpoint|100",
    ];

    // The one action both generations carry, so the v2 manifest is asserted whole rather than only
    // for what it lacks.
    private static readonly string[] ActionsOnBothGenerations =
    [
        "whisparr-monitor-selected-studios|bulk|studios|whisparrMonitorSelected|no endpoint|100",
    ];

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task V3RegistersEveryLibrarySurface()
    {
        var surfaces = await SurfacesForAsync(WhisparrGeneration.V3);

        Assert.Contains("studios-list-toolbar-end", surfaces.Slots);
        Assert.Contains("studio-card-footer", surfaces.Slots);
        Assert.All(SlotsAbsentOnV2, slot => Assert.Contains(slot, surfaces.Slots));

        // Equality rather than containment, so a second video tab is reported as well as an absent
        // one.
        Assert.Equal(TabsAbsentOnV2.Order(), TabsAbsentOnV2Order(surfaces.Tabs));
        Assert.Equal(
            ActionsAbsentOnV2.Concat(ActionsOnBothGenerations).Order(),
            surfaces.Actions.Order());
    }

    // A control that draws and then refuses is the defect this pins: the performer surfaces read as
    // offered on v2 and answered nothing, because both sat outside the conditional block.
    [Fact]
    public async Task V2RegistersTheStudioSurfacesAndNoPerformerOrSceneOne()
    {
        var surfaces = await SurfacesForAsync(WhisparrGeneration.V2);

        Assert.Contains("studios-list-toolbar-end", surfaces.Slots);
        Assert.Contains("studio-card-footer", surfaces.Slots);
        Assert.All(SlotsAbsentOnV2, slot => Assert.DoesNotContain(slot, surfaces.Slots));
        Assert.DoesNotContain(surfaces.Slots, slot => slot.Contains("performer", StringComparison.Ordinal));
        Assert.Empty(TabsAbsentOnV2Order(surfaces.Tabs));
        Assert.Equal(ActionsOnBothGenerations, surfaces.Actions);
    }

    // A generation-conditional block written too wide or too narrow is reported here rather than
    // in the browser.
    [Fact]
    public async Task TheTwoManifestsDifferInTheV3OnlySurfacesAndInNothingElse()
    {
        var v3 = await SurfacesForAsync(WhisparrGeneration.V3);
        var v2 = await SurfacesForAsync(WhisparrGeneration.V2);

        Assert.Equal(SlotsAbsentOnV2.Order(), v3.Slots.Except(v2.Slots).Order());
        Assert.Empty(v2.Slots.Except(v3.Slots));

        Assert.Equal(TabsAbsentOnV2.Order(), v3.Tabs.Except(v2.Tabs).Order());
        Assert.Empty(v2.Tabs.Except(v3.Tabs));

        Assert.Equal(ActionsAbsentOnV2.Order(), v3.Actions.Except(v2.Actions).Order());
        Assert.Empty(v2.Actions.Except(v3.Actions));
    }

    // A store nothing has written to establishes no generation and keeps every surface. A user who
    // has not configured the extension yet is not a user on v2.
    [Fact]
    public async Task AGenerationNeverStoredRegistersEverySurface()
    {
        var v3 = await SlotsForAsync(WhisparrGeneration.V3);

        var slots = await SlotsOfAsync(new FakeStore());

        Assert.Equal(v3.Order(), slots.Order());
    }

    // The host ships a route that writes an extension's store directly and reaches no code here. A
    // manifest refreshed only where this extension saves keeps registering the previous
    // generation's surfaces, and every badge on them refuses.
    [Fact]
    public async Task AGenerationWrittenStraightIntoTheStoreReachesTheManifestOnTheNextLoad()
    {
        var store = new FakeStore();
        await new OptionsStore(store)
            .SaveAsync(new WhisparrSyncOptions { SelectedGeneration = WhisparrGeneration.V3 }, TestCt);
        await using var loaded = await LoadedOverAsync(store);
        Assert.All(SlotsAbsentOnV2, slot => Assert.Contains(slot, SlotsOf(loaded.Extension)));

        // Written the way the host's own extension-data route writes it, so nothing of this
        // extension's own save path runs.
        await new OptionsStore(store)
            .SaveAsync(new WhisparrSyncOptions { SelectedGeneration = WhisparrGeneration.V2 }, TestCt);
        using var scope = loaded.Provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OptionsStore>().LoadAsync(TestCt);

        var slots = SlotsOf(loaded.Extension);
        Assert.Contains("studios-list-toolbar-end", slots);
        Assert.All(SlotsAbsentOnV2, slot => Assert.DoesNotContain(slot, slots));
    }

    // The load answers an unbindable blob with manufactured defaults, and the default names v3.
    // Publishing that would register every v3 surface on an instance the blob names as v2.
    [Fact]
    public async Task ABlobTheModelCannotBindEstablishesNoGeneration()
    {
        var store = new FakeStore();
        await store.SetAsync(OptionsStore.Key, """{"SelectedGeneration": "v2", "V3": 5}""", TestCt);

        var published = new List<string?>();
        await new OptionsStore(store, null, published.Add).LoadAsync(TestCt);

        Assert.Equal([null], published);
    }

    // An empty store is not the same case as a blob that failed to bind: its defaults are the
    // answer.
    [Fact]
    public async Task AStoreNothingHasWrittenToEstablishesV3()
    {
        var published = new List<string?>();
        await new OptionsStore(new FakeStore(), null, published.Add).LoadAsync(TestCt);

        Assert.Equal([nameof(WhisparrGeneration.V3)], published);
    }

    // A row registered where no badge is counts nothing, and a page of badges with no row leaves
    // every glyph on it unnamed.
    [Fact]
    public async Task EveryPageWithCardBadgesCarriesTheFullWidthRow()
    {
        foreach (var generation in new[] { WhisparrGeneration.V3, WhisparrGeneration.V2 })
        {
            var slots = await SlotsForAsync(generation);
            var pagesWithBadges = slots
                .Where(slot => slot.EndsWith("-card-footer", StringComparison.Ordinal)
                    || slot.EndsWith("-card-content", StringComparison.Ordinal))
                .Select(slot => slot.Split('-')[0])
                .Order();
            var pagesWithARow = slots
                .Where(slot => slot.EndsWith("-list-row", StringComparison.Ordinal))
                .Select(slot => slot.Split('-')[0].TrimEnd('s'))
                .Order();

            Assert.Equal(pagesWithBadges, pagesWithARow);
        }
    }

    private static async Task<RegisteredSurfaces> SurfacesForAsync(WhisparrGeneration generation)
    {
        var store = new FakeStore();
        await new OptionsStore(store)
            .SaveAsync(new WhisparrSyncOptions { SelectedGeneration = generation }, TestCt);
        await using var loaded = await LoadedOverAsync(store);
        return new RegisteredSurfaces(
            SlotsOf(loaded.Extension),
            TabsOf(loaded.Extension),
            ActionsOf(loaded.Extension));
    }

    private static async Task<IReadOnlyList<string>> SlotsForAsync(WhisparrGeneration generation)
        => (await SurfacesForAsync(generation)).Slots;

    // The tabs a v2 manifest must not carry, read out of whichever manifest was projected.
    private static IReadOnlyList<string> TabsAbsentOnV2Order(IReadOnlyList<string> tabs)
        => [.. tabs.Where(tab => TabsAbsentOnV2.Contains(tab, StringComparer.Ordinal)).Order()];

    // Loaded through InitializeAsync the way the host loads it, over the extension's own options
    // store, so the field the manifest reads is filled by the shipped path and not by the test.
    private static async Task<IReadOnlyList<string>> SlotsOfAsync(FakeStore store)
    {
        await using var loaded = await LoadedOverAsync(store);
        return SlotsOf(loaded.Extension);
    }

    private static IReadOnlyList<string> SlotsOf(global::WhisparrSync.WhisparrSync extension)
        => [.. extension.GetUIManifest().Slots.Select(slot => slot.Slot)];

    // The page type leads, because it tells apart a tab registered on one page type from another.
    // The count route and the glyph are carried so their absence is asserted rather than assumed.
    private static IReadOnlyList<string> TabsOf(global::WhisparrSync.WhisparrSync extension)
        => [.. extension.GetUIManifest().Tabs.Select(tab => string.Join(
            '|',
            tab.PageType,
            tab.Key,
            tab.Label,
            tab.ComponentName,
            tab.Order.ToString(CultureInfo.InvariantCulture),
            tab.CountEndpoint ?? "no count route",
            tab.Icon ?? "no glyph"))];

    private static IReadOnlyList<string> ActionsOf(global::WhisparrSync.WhisparrSync extension)
        => [.. extension.GetUIManifest().Actions.Select(action => string.Join(
            '|',
            action.Id,
            action.ActionType,
            string.Join(',', action.EntityTypes),
            action.HandlerName ?? "no handler",
            action.ApiEndpoint ?? "no endpoint",
            action.Order.ToString(CultureInfo.InvariantCulture)))];

    private static async Task<LoadedExtension> LoadedOverAsync(FakeStore store)
    {
        var extension = WhisparrSyncFixture.Create();
        ((IStatefulExtension)extension).SetStore(store);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        // These lifecycle tests resolve host dependencies but execute no database queries.
        services.AddScoped(_ => new DbContext(new DbContextOptionsBuilder().Options));
        extension.ConfigureServices(services, new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = AppContext.BaseDirectory,
            CoveVersion = WhisparrSyncFixture.Manifest.MinCoveVersion
                ?? throw new InvalidOperationException("The manifest must declare its host floor."),
        });
        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        try
        {
            await extension.InitializeAsync(provider, TestCt);
            return new LoadedExtension(extension, provider);
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }

    private sealed record RegisteredSurfaces(
        IReadOnlyList<string> Slots,
        IReadOnlyList<string> Tabs,
        IReadOnlyList<string> Actions);

    private sealed record LoadedExtension(
        global::WhisparrSync.WhisparrSync Extension, ServiceProvider Provider) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }
}
