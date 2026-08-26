using Cove.Core.Entities;
using Cove.Plugins;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Options;

/// <summary>
/// The initialize-time seam that drives the options conversion, through the real entry path with a
/// real database rather than the pure converter alone.
/// </summary>
/// <remarks>
/// The subject is the DEFERRAL. The converter cannot tell an entity that was deleted from a table it
/// cannot read yet, so the decision not to convert lives here, and getting it wrong destroys the
/// user's entity rules with nothing observable happening.
/// </remarks>
public sealed class OptionsMigrationInitializeTests
{
    private const string LegacyBlob = """
        {"FilenameTemplate":"$title","ExcludeTags":["spoiler"],"TagDestinations":{"drama":"/drama"}}
        """;

    private static async Task<global::Renamer.Renamer> LoadAsync(LibraryDatabase library, FakeStore store)
    {
        var ext = new global::Renamer.Renamer();
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(library.BuildProvider());
        return ext;
    }

    private static async Task SeedTagsAsync(LibraryDatabase library, params (int Id, string Name)[] tags)
    {
        await using var db = library.NewContext();
        foreach (var (id, name) in tags)
        {
            db.Add(new Tag { Id = id, Name = name });
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task NoTagRows_LeavesTheBlobAloneAndDoesNotStamp()
    {
        // The whole point of the deferral: converting against an unreadable table resolves every name
        // to nothing and writes the user's rules away permanently, because the stamp would stop it
        // ever being retried.
        await using var library = await LibraryDatabase.CreateAsync();
        var store = new FakeStore();
        await store.SetAsync(OptionsStore.Key, LegacyBlob);

        await LoadAsync(library, store);

        Assert.Equal(LegacyBlob, await store.GetAsync(OptionsStore.Key));
        Assert.Null(await store.GetAsync(OptionsMigration.SchemaKey));
    }

    [Fact]
    public async Task ALaterLoadOnceTheTableIsReadable_Converts()
    {
        // The deferral has to be a retry rather than a give-up, or the first load on an empty library
        // would leave a permanently unreadable configuration.
        await using var library = await LibraryDatabase.CreateAsync();
        var store = new FakeStore();
        await store.SetAsync(OptionsStore.Key, LegacyBlob);

        await LoadAsync(library, store);
        Assert.Null(await store.GetAsync(OptionsMigration.SchemaKey));

        await SeedTagsAsync(library, (13, "spoiler"), (14, "drama"));
        await LoadAsync(library, store);

        Assert.Equal(OptionsMigration.CurrentSchema, await store.GetAsync(OptionsMigration.SchemaKey));
        var options = await new OptionsStore(store).LoadAsync();
        Assert.Equal([13], options.ExcludeTagIds);
        Assert.Equal("/drama", options.TagDestinations[14]);
    }

    [Fact]
    public async Task PopulatedTable_ConvertsOnTheFirstLoad()
    {
        await using var library = await LibraryDatabase.CreateAsync();
        await SeedTagsAsync(library, (13, "spoiler"), (14, "drama"));
        var store = new FakeStore();
        await store.SetAsync(OptionsStore.Key, LegacyBlob);

        await LoadAsync(library, store);

        var options = await new OptionsStore(store).LoadAsync();
        Assert.Equal([13], options.ExcludeTagIds);
        Assert.Equal("/drama", options.TagDestinations[14]);
        Assert.Equal("$title", options.FilenameTemplate);
    }

    [Fact]
    public async Task AlreadyStamped_IsNotConvertedAgain()
    {
        // The stamp is the only thing making the conversion one-time, so a stamped blob must be left
        // exactly as it is even when it still looks legacy.
        await using var library = await LibraryDatabase.CreateAsync();
        await SeedTagsAsync(library, (13, "spoiler"), (14, "drama"));
        var store = new FakeStore();
        await store.SetAsync(OptionsStore.Key, LegacyBlob);
        await store.SetAsync(OptionsMigration.SchemaKey, OptionsMigration.CurrentSchema);

        await LoadAsync(library, store);

        Assert.Equal(LegacyBlob, await store.GetAsync(OptionsStore.Key));
    }

    [Fact]
    public async Task NoStoredOptions_WritesNothing()
    {
        await using var library = await LibraryDatabase.CreateAsync();
        var store = new FakeStore();

        await LoadAsync(library, store);

        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public async Task AlreadyIdKeyedBlob_IsStampedAndLeftIntact()
    {
        // An install that saved from a panel already speaking ids has nothing to convert, and its rules
        // must survive the pass that records that.
        const string current = """
            {"FilenameTemplate":"$title","ExcludeTagIds":[13],"TagDestinations":{"14":"/drama"}}
            """;
        await using var library = await LibraryDatabase.CreateAsync();
        var store = new FakeStore();
        await store.SetAsync(OptionsStore.Key, current);

        await LoadAsync(library, store);

        Assert.Equal(OptionsMigration.CurrentSchema, await store.GetAsync(OptionsMigration.SchemaKey));
        var options = await new OptionsStore(store).LoadAsync();
        Assert.Equal([13], options.ExcludeTagIds);
        Assert.Equal("/drama", options.TagDestinations[14]);
    }

    [Fact]
    public async Task AnUnresolvableRule_IsDroppedWhileTheRestSurvives()
    {
        await using var library = await LibraryDatabase.CreateAsync();
        await SeedTagsAsync(library, (14, "drama"));
        var store = new FakeStore();
        await store.SetAsync(OptionsStore.Key, LegacyBlob);

        await LoadAsync(library, store);

        var options = await new OptionsStore(store).LoadAsync();
        Assert.Empty(options.ExcludeTagIds);
        Assert.Equal("/drama", options.TagDestinations[14]);
    }
}
