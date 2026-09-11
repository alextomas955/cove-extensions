using System.Globalization;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Library;

/// <summary>
/// Which slots, tabs and selection actions
/// <see cref="global::WhisparrSync.WhisparrSync.GetUIManifest"/> registers for each stored
/// generation.
/// </summary>
/// <remarks>
/// The browser fetches the manifest, and a slot nothing registers makes the host render no wrapper
/// element at all, so an omission here is what removes a surface from the page. A component that
/// returned nothing would leave the wrapper behind.
/// <para>
/// All three groups are projected, because a registration written outside the generation-conditional
/// block reaches both manifests and a slot-only projection agrees with itself about that.
/// </para>
/// <para>
/// The two sets are compared as sets. A count agrees with itself when one registration is swapped
/// for another.
/// </para>
/// </remarks>
public sealed class ManifestGenerationTests
{
    /// <summary>
    /// The surfaces v2 has no meaning for: it publishes no per-scene identity and
    /// holds no performer entity.
    /// </summary>
    private static readonly string[] VideosViewSlots =
    [
        "videos-list-toolbar-end",
        "video-card-content",
        "videos-list-row",
        "performers-list-toolbar-end",
        "performer-card-footer",
        "performers-list-row",
    ];

    /// <inheritdoc cref="VideosViewSlots"/>
    /// <remarks>
    /// The whole tuple, so a count route or a glyph added to the registration is reported here. The
    /// video detail page keeps only the key, the label and the manual contexts of a contributed tab,
    /// so either would be fetched and drawn by nothing.
    /// </remarks>
    private static readonly string[] VideosViewTabs =
    [
        "video|whisparr-scene|Whisparr|WhisparrSceneTab|150|no count route|no glyph",
    ];

    /// <inheritdoc cref="VideosViewSlots"/>
    /// <remarks>
    /// The whole tuple, so the entity type is asserted as the SINGULAR spelling the host's selection
    /// bar passes for a video selection. The plural would make the button simply not appear, with no
    /// error anywhere. The absent endpoint is carried too, because the handler has to ask for a verb
    /// before anything is sent.
    /// </remarks>
    private static readonly string[] VideosViewActions =
    [
        "whisparr-scene-batch|bulk|video|whisparrSceneBatch|no endpoint|100",
    ];

    /// <summary>Which prefix of a projected tab names the video detail page.</summary>
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

    /// <summary>
    /// The two manifests differ in exactly the videos-view surfaces, so a conditional block written
    /// too wide or too narrow is reported here rather than in the browser.
    /// </summary>
    [Fact]
    public async Task TheTwoManifestsDifferInTheVideosViewSurfacesAndInNothingElse()
    {
        var newer = await SurfacesForAsync(WhisparrGeneration.V3);
        var older = await SurfacesForAsync(WhisparrGeneration.V2);

        Assert.Equal(VideosViewSlots.Order(), newer.Slots.Except(older.Slots).Order());
        Assert.Empty(older.Slots.Except(newer.Slots));

        Assert.Equal(VideosViewTabs.Order(), newer.Tabs.Except(older.Tabs).Order());
        Assert.Empty(older.Tabs.Except(newer.Tabs));

        Assert.Equal(VideosViewActions.Order(), newer.Actions.Except(older.Actions).Order());
        Assert.Empty(older.Actions.Except(newer.Actions));
    }

    /// <summary>
    /// A store nothing has written to is a generation not established, which keeps every surface: a
    /// user who has not configured the extension yet is not a user on v2.
    /// </summary>
    [Fact]
    public async Task AGenerationNeverStoredRegistersEverySurface()
    {
        var newer = await SlotsForAsync(WhisparrGeneration.V3);

        var slots = await SlotsOfAsync(new FakeStore());

        Assert.Equal(newer.Order(), slots.Order());
    }

    /// <summary>
    /// A generation written straight into the store reaches the manifest on the next load.
    /// </summary>
    /// <remarks>
    /// The blob has writers this extension never sees: the host ships a route that writes an
    /// extension's store directly and reaches no code here. A manifest refreshed only where this
    /// extension saves keeps registering the previous generation's surfaces, and every badge on them
    /// refuses.
    /// </remarks>
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

    /// <summary>
    /// A stored blob the model cannot bind establishes no generation, rather than the default one.
    /// </summary>
    /// <remarks>
    /// The load answers such a blob with manufactured defaults, and the default names the newer
    /// generation. Publishing that would register every v3 surface on an instance the
    /// blob names as v2, with full confidence and on a value no user configured.
    /// <para>
    /// Observed through the callback the extension's own store factory hands in, which is the only
    /// reader of the published value a test can stand beside.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ABlobTheModelCannotBindEstablishesNoGeneration()
    {
        var store = new FakeStore();
        await store.SetAsync(OptionsStore.Key, """{"SelectedGeneration": "v2", "V3": 5}""", TestCt);

        var published = new List<string?>();
        await new OptionsStore(store, null, published.Add).LoadAsync(TestCt);

        Assert.Equal([null], published);
    }

    /// <summary>A store nothing has written to establishes the default, which is v3.</summary>
    /// <remarks>
    /// Its defaults are the answer, so it is not the same case as a blob that failed to bind. This is
    /// the input the never-stored manifest case runs on, named here so the two are not confused.
    /// </remarks>
    [Fact]
    public async Task AStoreNothingHasWrittenToEstablishesTheNewerGeneration()
    {
        var published = new List<string?>();
        await new OptionsStore(new FakeStore(), null, published.Add).LoadAsync(TestCt);

        Assert.Equal([nameof(WhisparrGeneration.V3)], published);
    }

    /// <summary>
    /// The full-width row goes with the card badges it counts, on every page that has them.
    /// </summary>
    /// <remarks>
    /// A row registered where no badge is is a row of counts over nothing, and a page of badges with
    /// no row leaves every glyph on it unnamed.
    /// </remarks>
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

    /// <summary>Every surface the manifest registers for a stored <paramref name="generation"/>.</summary>
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

    /// <summary>Every slot the manifest registers for a stored <paramref name="generation"/>.</summary>
    private static async Task<IReadOnlyList<string>> SlotsForAsync(WhisparrGeneration generation)
        => (await SurfacesForAsync(generation)).Slots;

    /// <summary>The projected tabs of <paramref name="tabs"/> that name the video detail page.</summary>
    private static IReadOnlyList<string> VideoTabsIn(IReadOnlyList<string> tabs)
        => [.. tabs.Where(tab => tab.StartsWith(VideoTabPrefix, StringComparison.Ordinal))];

    /// <summary>
    /// The projected actions of <paramref name="actions"/> that a video selection offers.
    /// </summary>
    /// <remarks>
    /// Matched on the entity type the host's selection bar passes, which is the SINGULAR spelling
    /// for a video selection while a studio or performer selection arrives plural.
    /// </remarks>
    private static IReadOnlyList<string> VideoActionsIn(IReadOnlyList<string> actions)
        => [.. actions.Where(action => action.Contains("|video|", StringComparison.Ordinal))];

    /// <summary>
    /// Every slot the manifest registers for an extension loaded against <paramref name="store"/>.
    /// </summary>
    /// <remarks>
    /// Loaded through <c>InitializeAsync</c> the way the host loads it, and over the extension's own
    /// options store, so the field the manifest reads is filled by the shipped path and not by the
    /// test.
    /// </remarks>
    private static async Task<IReadOnlyList<string>> SlotsOfAsync(FakeStore store)
    {
        await using var loaded = await LoadedOverAsync(store);
        return SlotsOf(loaded.Extension);
    }

    private static IReadOnlyList<string> SlotsOf(global::WhisparrSync.WhisparrSync extension)
        => [.. extension.GetUIManifest().Slots.Select(slot => slot.Slot)];

    /// <summary>Every tab the manifest registers, as one comparable string each.</summary>
    /// <remarks>
    /// The page type leads, because it is what a tab registered on one page type and not another is
    /// told apart by, and the count route and the glyph are carried so their ABSENCE is asserted
    /// rather than assumed.
    /// </remarks>
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

    /// <summary>Every selection action the manifest registers, as one comparable string each.</summary>
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

    /// <summary>The three registration groups one manifest carries, each already projected.</summary>
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
