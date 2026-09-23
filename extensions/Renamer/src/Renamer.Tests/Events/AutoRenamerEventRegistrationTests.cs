using Cove.Plugins;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

public sealed class AutoRenamerEventRegistrationTests
{
    private const string OptionsKey = "options";

    private static async Task<FakeStore> DispatchAsync(string eventType, string entityType)
    {
        var extension = RenamerFixture.Create();
        var store = new FakeStore();
        ((IStatefulExtension)extension).SetStore(store);

        await ((IEventExtension)extension).OnEventAsync(
            new ExtensionEvent(eventType, entityType, EntityId: 1),
            CancellationToken.None);

        return store;
    }

    [Theory]
    [InlineData("video.updated", "video")]
    [InlineData("image.updated", "image")]
    public async Task AHookedKindReachesTheHandler(string eventType, string entityType)
    {
        var store = await DispatchAsync(eventType, entityType);

        Assert.Contains(OptionsKey, store.GetKeys);
    }

    // Audio and text are renamable through the manual job/API surface but are deliberately not hooked
    // to per-edit events; gallery is not renamable at all. Adding one here without meaning to would
    // give every metadata edit of that kind an unconfirmed, unpreviewed rename.
    [Theory]
    [InlineData("audio.updated", "audio")]
    [InlineData("gallery.updated", "gallery")]
    [InlineData("text.updated", "text")]
    public async Task AnUnhookedKindReachesNothing(string eventType, string entityType)
    {
        var store = await DispatchAsync(eventType, entityType);

        Assert.DoesNotContain(OptionsKey, store.GetKeys);
    }

    // The hook is scoped to updates. A created/deleted event must not act: rename-on-create would
    // fight the scanner mid-ingest, and rename-on-delete has nothing left to rename.
    [Theory]
    [InlineData("video.created", "video")]
    [InlineData("video.deleted", "video")]
    [InlineData("image.created", "image")]
    [InlineData("image.deleted", "image")]
    public async Task OnlyUpdatesAreHooked(string eventType, string entityType)
    {
        var store = await DispatchAsync(eventType, entityType);

        Assert.DoesNotContain(OptionsKey, store.GetKeys);

        // These kinds are hooked, so the verb is the only reason nothing happened. Showing the same
        // kind's update still reaches the handler is what separates "only updates are hooked" from
        // "this kind is not hooked at all" - two states the assertion above cannot tell apart.
        var updated = await DispatchAsync($"{entityType}.updated", entityType);

        Assert.Contains(OptionsKey, updated.GetKeys);
    }
}
