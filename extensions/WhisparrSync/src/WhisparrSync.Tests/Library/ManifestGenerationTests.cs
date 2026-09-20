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
    // The surfaces v2 has no meaning for. It publishes no per-scene identity and holds no
    // performer entity.
    private static readonly string[] VideosViewSlots =
    [
        "videos-list-toolbar-end",
        "video-card-content",
        "videos-list-row",
        "performers-list-toolbar-end",
        "performer-card-footer",
        "performers-list-row",
    ];

    // The whole tuple, so a count route or a glyph added to the registration is reported here. The
    // video detail page keeps only a contributed tab's key, label and manual contexts, so either
    // would be fetched and drawn by nothing.
    private static readonly string[] VideosViewTabs =
    [
        "video|whisparr-scene|Whisparr|WhisparrSceneTab|150|no count route|no glyph",
    ];

    // The entity type is the singular spelling the host's selection bar passes for a video
    // selection. The plural makes the button not appear, with no error anywhere.
    private static readonly string[] VideosViewActions =
    [
        "whisparr-scene-batch|bulk|video|whisparrSceneBatch|no endpoint|100",
    ];

    private const string VideoTabPrefix = "video|";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheNewerGenerationRegistersEveryLibrarySurface()
    {
        var surfaces = await SurfacesForAsync(WhisparrGeneration.V3);

        Assert.Contains("studios-list-toolbar-end", surfaces.Slots);
        Assert.Contains("studio-card-footer", surfaces.Slots);
        Assert.All(VideosViewSlots, slot => Assert.Contains(slot, surfaces.Slots));

        // Equality rather than containment, so a second video tab is reported as well as an absent
        // one.
        Assert.Equal(VideosViewTabs, VideoTabsIn(surfaces.Tabs));
        Assert.Equal(VideosViewActions, VideoActionsIn(surfaces.Actions));
    }

    [Fact]
    public async Task TheOlderGenerationRegistersTheStudioSurfacesAndNoneOfTheVideosViewOnes()
    {
        var surfaces = await SurfacesForAsync(WhisparrGeneration.V2);

        Assert.Contains("studios-list-toolbar-end", surfaces.Slots);
        Assert.Contains("studio-card-footer", surfaces.Slots);
        Assert.All(VideosViewSlots, slot => Assert.DoesNotContain(slot, surfaces.Slots));
        Assert.Empty(VideoTabsIn(surfaces.Tabs));
        Assert.Empty(VideoActionsIn(surfaces.Actions));
    }

    // A generation-conditional block written too wide or too narrow is reported here rather than
    // in the browser.
    [Fact]
    public async Task TheTwoManifestsDifferInTheVideosViewSurfacesAndInNothingElse()
    {
        var v3 = await SurfacesForAsync(WhisparrGeneration.V3);
        var v2 = await SurfacesForAsync(WhisparrGeneration.V2);

        Assert.Equal(VideosViewSlots.Order(), v3.Slots.Except(v2.Slots).Order());
        Assert.Empty(v2.Slots.Except(v3.Slots));

        Assert.Equal(VideosViewTabs.Order(), v3.Tabs.Except(v2.Tabs).Order());
        Assert.Empty(v2.Tabs.Except(v3.Tabs));

        Assert.Equal(VideosViewActions.Order(), v3.Actions.Except(v2.Actions).Order());
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
        Assert.All(VideosViewSlots, slot => Assert.Contains(slot, SlotsOf(loaded.Extension)));

        // Written the way the host's own extension-data route writes it, so nothing of this
        // extension's own save path runs.
        await new OptionsStore(store)
            .SaveAsync(new WhisparrSyncOptions { SelectedGeneration = WhisparrGeneration.V2 }, TestCt);
        using var scope = loaded.Provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OptionsStore>().LoadAsync(TestCt);

        var slots = SlotsOf(loaded.Extension);
        Assert.Contains("studios-list-toolbar-end", slots);
        Assert.All(VideosViewSlots, slot => Assert.DoesNotContain(slot, slots));
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
    public async Task AStoreNothingHasWrittenToEstablishesTheNewerGeneration()
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

    private static IReadOnlyList<string> VideoTabsIn(IReadOnlyList<string> tabs)
        => [.. tabs.Where(tab => tab.StartsWith(VideoTabPrefix, StringComparison.Ordinal))];

    // Matched on the entity type the host's selection bar passes, the singular spelling for a
    // video selection while a studio or performer selection arrives plural.
    private static IReadOnlyList<string> VideoActionsIn(IReadOnlyList<string> actions)
        => [.. actions.Where(action => action.Contains("|video|", StringComparison.Ordinal))];

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
