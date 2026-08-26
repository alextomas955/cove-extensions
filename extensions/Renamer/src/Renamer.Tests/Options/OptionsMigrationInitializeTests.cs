using Cove.Core.Entities;
using Cove.Plugins;
using Microsoft.Extensions.Logging;
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
[Trait("Tier", "L1")]
public sealed class OptionsMigrationInitializeTests
{
    /// <summary>The library path every stored destination in these blobs lives under.</summary>
    private const string DramaRoot = "/drama";

    private const string LegacyBlob = """
        {"FilenameTemplate":"$title","ExcludeTags":["spoiler"],"TagDestinations":{"drama":"/drama"}}
        """;

    private static async Task<global::Renamer.Renamer> LoadAsync(
        LibraryDatabase library, FakeStore store, params string[] libraryPaths)
    {
        var ext = new global::Renamer.Renamer();
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(library.BuildProvider(libraryPaths));
        return ext;
    }

    /// <summary>
    /// Loads against a database holding the undo journal and NOTHING else, so the load itself completes
    /// and every library read throws. A conversion that reached one is visible as the seam's failure path.
    /// </summary>
    private static async Task LoadWithoutLibraryTablesAsync(JournalOnlyDatabase db, IExtensionStore store)
    {
        var ext = new global::Renamer.Renamer();
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(db.BuildProvider());
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

        await LoadAsync(library, store, DramaRoot);

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

        await LoadAsync(library, store, DramaRoot);
        Assert.Null(await store.GetAsync(OptionsMigration.SchemaKey));

        await SeedTagsAsync(library, (13, "spoiler"), (14, "drama"));
        await LoadAsync(library, store, DramaRoot);

        Assert.Equal(OptionsMigration.CurrentSchema, await store.GetAsync(OptionsMigration.SchemaKey));
        var options = await new OptionsStore(store).LoadAsync();
        Assert.Equal([13], options.ExcludeTagIds);
        Assert.Equal(DramaRoot, options.TagDestinations[14].Root);
    }

    [Fact]
    public async Task PopulatedTable_ConvertsOnTheFirstLoad()
    {
        await using var library = await LibraryDatabase.CreateAsync();
        await SeedTagsAsync(library, (13, "spoiler"), (14, "drama"));
        var store = new FakeStore();
        await store.SetAsync(OptionsStore.Key, LegacyBlob);

        await LoadAsync(library, store, DramaRoot);

        var options = await new OptionsStore(store).LoadAsync();
        Assert.Equal([13], options.ExcludeTagIds);
        Assert.Equal(DramaRoot, options.TagDestinations[14].Root);
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

        await LoadAsync(library, store, DramaRoot);

        Assert.Equal(LegacyBlob, await store.GetAsync(OptionsStore.Key));
    }

    [Fact]
    public async Task NoStoredOptions_WritesNothing()
    {
        await using var library = await LibraryDatabase.CreateAsync();
        var store = new FakeStore();

        await LoadAsync(library, store, DramaRoot);

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

        await LoadAsync(library, store, DramaRoot);

        Assert.Equal(OptionsMigration.CurrentSchema, await store.GetAsync(OptionsMigration.SchemaKey));
        var options = await new OptionsStore(store).LoadAsync();
        Assert.Equal([13], options.ExcludeTagIds);
        Assert.Equal(DramaRoot, options.TagDestinations[14].Root);
    }

    [Fact]
    public async Task AnUnresolvableRule_IsDroppedWhileTheRestSurvives()
    {
        await using var library = await LibraryDatabase.CreateAsync();
        await SeedTagsAsync(library, (14, "drama"));
        var store = new FakeStore();
        await store.SetAsync(OptionsStore.Key, LegacyBlob);

        await LoadAsync(library, store, DramaRoot);

        var options = await new OptionsStore(store).LoadAsync();
        Assert.Empty(options.ExcludeTagIds);
        Assert.Equal(DramaRoot, options.TagDestinations[14].Root);
    }

    [Fact]
    public async Task WhenTheLibraryReadThrows_WritesNothingAndDoesNotStamp()
    {
        // The deferral above covers a read that succeeds and is empty. A read that THROWS is the other
        // way the table can be unavailable, and it arrives as an exception the load has to step over
        // rather than as a value the conversion can inspect.
        var store = new FakeStore();
        await store.SetAsync(OptionsStore.Key, LegacyBlob);
        int setsBefore = store.SetCallCount;

        // The undo journal and nothing else, so the load completes and the conversion's own read throws.
        await using var db = await JournalOnlyDatabase.CreateAsync();
        await LoadWithoutLibraryTablesAsync(db, store);

        Assert.Equal(setsBefore, store.SetCallCount);
        Assert.Equal(LegacyBlob, await store.GetAsync(OptionsStore.Key));
        Assert.Null(await store.GetAsync(OptionsMigration.SchemaKey));
    }

    [Fact]
    public async Task ASecondLoadAgainstAnAlreadyStampedStore_WritesNothing()
    {
        // A second load is a host restart, redeploy or reboot. Asserting the blob is unchanged is not
        // enough on its own: a conversion that ran again and produced the same bytes would satisfy that
        // while re-reading and re-writing the user's settings on every start.
        await using var library = await LibraryDatabase.CreateAsync();
        await SeedTagsAsync(library, (13, "spoiler"), (14, "drama"));
        var store = new FakeStore();
        await store.SetAsync(OptionsStore.Key, LegacyBlob);

        await LoadAsync(library, store, DramaRoot);
        Assert.Equal(OptionsMigration.CurrentSchema, await store.GetAsync(OptionsMigration.SchemaKey));
        string? afterFirst = await store.GetAsync(OptionsStore.Key);
        int setsAfterFirst = store.SetCallCount;

        await LoadAsync(library, store, DramaRoot);

        Assert.Equal(setsAfterFirst, store.SetCallCount);
        Assert.Equal(afterFirst, await store.GetAsync(OptionsStore.Key));
    }

    [Fact]
    public async Task OnceStamped_TheStoredBlobIsNotEvenRead()
    {
        // What the stamp buys beyond the already-converted shape check: the stored blob is never read.
        // That value is the whole of the user's settings and can reach hundreds of megabytes, so the
        // difference between skipping the read and re-parsing it is paid on every single load.
        var store = new FakeStore();
        await store.SetAsync(OptionsStore.Key, LegacyBlob);
        await store.SetAsync(OptionsMigration.SchemaKey, OptionsMigration.CurrentSchema);
        store.GetKeys.Clear();

        // No library tables: a stamp that failed to short-circuit would read one and throw.
        await using var db = await JournalOnlyDatabase.CreateAsync();
        await LoadWithoutLibraryTablesAsync(db, store);

        Assert.DoesNotContain(OptionsStore.Key, store.GetKeys);
    }

    [Fact]
    public async Task AnAlreadyIdKeyedStore_IsStampedWithoutAnyLibraryRead()
    {
        // A fresh install already stores ids and destination objects. It must not pay for a library read,
        // and - the dangerous half - its int-spelled TagDestinations keys must never be re-read as NAMES.
        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(
            new RenamerOptions
            {
                TagDestinations = { [14] = Dest.At(DramaRoot, "$studio") },
                ExcludeTagIds = { 13 },
            });
        string? saved = await store.GetAsync(OptionsStore.Key);
        int setsBefore = store.SetCallCount;

        // No library tables, so any read throws; the seam answers a throw by NOT stamping. A stamp is
        // therefore proof the conversion reached its end without touching a library table.
        await using var db = await JournalOnlyDatabase.CreateAsync();
        await LoadWithoutLibraryTablesAsync(db, store);

        Assert.Equal(OptionsMigration.CurrentSchema, await store.GetAsync(OptionsMigration.SchemaKey));
        Assert.Equal(saved, await store.GetAsync(OptionsStore.Key));

        // The stamp is the one write: the settings blob was left alone rather than rewritten identically.
        Assert.Equal(setsBefore + 1, store.SetCallCount);

        var options = await new OptionsStore(store).LoadAsync();
        Assert.Equal(
            new Dictionary<int, Destination> { [14] = Dest.At(DramaRoot, "$studio") },
            options.TagDestinations);
        Assert.Equal([13], options.ExcludeTagIds);
    }

    [Fact]
    public async Task WhenTheStampWriteFails_TheRewriteAndItsDroppedNamesAreStillOnTheRecord()
    {
        // A store write is a database write and can fail on its own. The conversion keeps no copy of the
        // originals, so the dropped-name line is the only trace of what it discarded: recording it after
        // both writes would throw that trace away on exactly the run that rewrote the settings.
        var store = new FakeStore();
        await store.SetAsync(
            OptionsStore.Key, """{"ExcludeTags":["spoiler","tag-that-no-longer-exists"]}""");
        var failing = new FailingStampStore(store);

        await using var library = await LibraryDatabase.CreateAsync();
        await SeedTagsAsync(library, (13, "spoiler"));
        var log = new CapturingLogger();
        library.Log = log;

        var ext = new global::Renamer.Renamer();
        ((IStatefulExtension)ext).SetStore(failing);
        await ext.InitializeAsync(library.BuildProvider(DramaRoot));

        // The settings write landed, and the name it discarded is named.
        Assert.Equal([13], (await new OptionsStore(store).LoadAsync()).ExcludeTagIds);
        Assert.Contains(RuleDroppedEvent, log.Events);

        // Asserted at the event id rather than the wording, which would pin the sentence instead of the
        // behaviour: the failure is reported, and the store is left unstamped so a later load retries.
        Assert.Contains(MigrationFailedEvent, log.Events);
        Assert.Null(await store.GetAsync(OptionsMigration.SchemaKey));
    }

    /// <summary>The dropped-rule warning, the only trace of a rule the conversion discarded.</summary>
    private const int RuleDroppedEvent = 1068;

    /// <summary>The load-time catch that reports a conversion which could not complete.</summary>
    private const int MigrationFailedEvent = 1066;

    /// <summary>Fails only the schema stamp, so the settings write ahead of it still lands.</summary>
    private sealed class FailingStampStore(IExtensionStore inner) : IExtensionStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => inner.GetAsync(key, ct);

        public Task SetAsync(string key, string value, CancellationToken ct = default) =>
            key == OptionsMigration.SchemaKey
                ? throw new InvalidOperationException("the stamp write failed")
                : inner.SetAsync(key, value, ct);

        public Task DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);

        public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default) =>
            inner.GetAllAsync(ct);
    }

    /// <summary>Records the id of every event the load logged.</summary>
    private sealed class CapturingLogger : ILogger<global::Renamer.Renamer>
    {
        public List<int> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Events.Add(eventId.Id);
    }
}
