using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Library;

/// <summary>
/// Which slots <see cref="global::WhisparrSync.WhisparrSync.GetUIManifest"/> registers for each
/// stored generation.
/// </summary>
/// <remarks>
/// The browser fetches the manifest, and a slot nothing registers makes the host render no wrapper
/// element at all, so an omission here is what removes a surface from the page. A component that
/// returned nothing would leave the wrapper behind.
/// <para>
/// The two sets are compared as sets. A count agrees with itself when one registration is swapped
/// for another.
/// </para>
/// </remarks>
public sealed class ManifestGenerationTests
{
    /// <summary>
    /// The surfaces the older generation has no meaning for: it publishes no per-scene identity and
    /// holds no performer entity.
    /// </summary>
    private static readonly string[] VideosViewSlots =
    [
        "videos-list-toolbar-end",
        "video-card-content",
        "performers-list-toolbar-end",
        "performer-card-footer",
    ];

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheNewerGenerationRegistersEveryLibrarySurface()
    {
        var slots = await SlotsForAsync(WhisparrGeneration.V3);

        Assert.Contains("studios-list-toolbar-end", slots);
        Assert.Contains("studio-card-footer", slots);
        Assert.All(VideosViewSlots, slot => Assert.Contains(slot, slots));
    }

    [Fact]
    public async Task TheOlderGenerationRegistersTheStudioSurfacesAndNoneOfTheVideosViewOnes()
    {
        var slots = await SlotsForAsync(WhisparrGeneration.V2);

        Assert.Contains("studios-list-toolbar-end", slots);
        Assert.Contains("studio-card-footer", slots);
        Assert.All(VideosViewSlots, slot => Assert.DoesNotContain(slot, slots));
    }

    /// <summary>
    /// The two manifests differ in exactly the videos-view surfaces, so a conditional block written
    /// too wide or too narrow is reported here rather than in the browser.
    /// </summary>
    [Fact]
    public async Task TheTwoManifestsDifferInTheVideosViewSurfacesAndInNothingElse()
    {
        var newer = await SlotsForAsync(WhisparrGeneration.V3);
        var older = await SlotsForAsync(WhisparrGeneration.V2);

        Assert.Equal(VideosViewSlots.Order(), newer.Except(older).Order());
        Assert.Empty(older.Except(newer));
    }

    /// <summary>
    /// A store nothing has written to is a generation not established, which keeps every surface: a
    /// user who has not configured the extension yet is not a user on the older generation.
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

    [Fact]
    public async Task NeitherGenerationOccupiesTheHostsFullWidthRowSlot()
    {
        foreach (var generation in new[] { WhisparrGeneration.V3, WhisparrGeneration.V2 })
        {
            Assert.All(
                await SlotsForAsync(generation),
                slot => Assert.DoesNotContain("-list-row", slot, StringComparison.Ordinal));
        }
    }

    /// <summary>Every slot the manifest registers for a stored <paramref name="generation"/>.</summary>
    private static async Task<IReadOnlyList<string>> SlotsForAsync(WhisparrGeneration generation)
    {
        var store = new FakeStore();
        await new OptionsStore(store)
            .SaveAsync(new WhisparrSyncOptions { SelectedGeneration = generation }, TestCt);
        return await SlotsOfAsync(store);
    }

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

    /// <summary>
    /// An extension initialized against <paramref name="store"/> through its own registration, and
    /// the container it resolves its options store from.
    /// </summary>
    /// <remarks>
    /// The store registration is the extension's own factory rather than one the test composes, so
    /// what a load publishes is what the shipped wiring publishes.
    /// </remarks>
    private static async Task<LoadedExtension> LoadedOverAsync(FakeStore store)
    {
        var extension = WhisparrSyncFixture.Create();
        ((IStatefulExtension)extension).SetStore(store);

        var services = new ServiceCollection();
        services.AddScoped(_ => extension.NewOptionsStore());
        var provider = services.BuildServiceProvider();

        await extension.InitializeAsync(provider, TestCt);
        return new LoadedExtension(extension, provider);
    }

    private sealed record LoadedExtension(
        global::WhisparrSync.WhisparrSync Extension, ServiceProvider Provider) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }
}
