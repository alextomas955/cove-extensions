using System.Text.Json;
using Microsoft.Extensions.Logging;
using Renamer.Options;

namespace Renamer.Tests.Options;

public sealed class OptionsStoreTests
{
    [Fact]
    public async Task LoadAsync_AbsentKey_ReturnsDefaults()
    {
        var store = new OptionsStore(new FakeStore());

        var loaded = await store.LoadAsync();

        Assert.Equal(new RenamerOptions(), loaded); // first run → defaults
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsEqual()
    {
        var fake = new FakeStore();
        var store = new OptionsStore(fake);
        var custom = new RenamerOptions
        {
            FilenameTemplate = "$studio - $title",
            Case = CaseTransform.Lower,
            FilenameMax = 120,
            Tags = new MultiValueOptions { Separator = "_", MaxCount = 2, OnOverflow = OverflowPolicy.KeepFirst },
            DropOrder = ["tags", "title"],
        };

        await store.SaveAsync(custom);
        var loaded = await store.LoadAsync();

        Assert.Equal(custom, loaded);
    }

    /// <summary>
    /// An explicitly-null collection in the stored blob loads as its default, not as null.
    /// </summary>
    /// <remarks>
    /// An init-default applies only to an ABSENT key, so <c>"DropOrder": null</c> binds to null and the
    /// safe-defaults contract is defeated. It is not a load-time crash either: the store catches only
    /// <see cref="JsonException"/>, so the null escapes and NREs later, in the planner, on the first
    /// file. Asserted through the real <c>LoadAsync</c> rather than a deserialize call, because the
    /// coalescing has to sit on the load path every caller uses.
    /// </remarks>
    [Fact]
    public async Task LoadAsync_ExplicitlyNullCollection_LoadsItsDefault_NotNull()
    {
        var fake = new FakeStore();
        await fake.SetAsync("options", """{"FilenameTemplate":"$title","DropOrder":null,"AssociatedExtensions":null}""");
        var store = new OptionsStore(fake);

        var loaded = await store.LoadAsync();

        // The blob's real value is kept; only the nulls are replaced.
        Assert.Equal("$title", loaded.FilenameTemplate);
        Assert.NotNull(loaded.DropOrder);
        Assert.NotNull(loaded.AssociatedExtensions);
        Assert.Equal(new RenamerOptions().DropOrder, loaded.DropOrder);
    }

    /// <summary>
    /// A stored negative length cap is clamped on load, so a rendered name can never be emptied by it.
    /// </summary>
    /// <remarks>
    /// A negative cap does not throw: the reducer clamps the computed budget to zero, which yields a
    /// deterministic EMPTY name — worse than a throw, because it looks like a result.
    /// </remarks>
    [Fact]
    public async Task LoadAsync_NegativeLengthCaps_AreClamped()
    {
        var fake = new FakeStore();
        await fake.SetAsync("options", """{"FilenameMax":-5,"FullPathMax":-100}""");
        var store = new OptionsStore(fake);

        var loaded = await store.LoadAsync();

        Assert.True(loaded.FilenameMax > 0, $"FilenameMax must be positive, was {loaded.FilenameMax}");
        Assert.True(loaded.FullPathMax > 0, $"FullPathMax must be positive, was {loaded.FullPathMax}");
    }

    [Fact]
    public async Task LoadAsync_CorruptBlob_ReturnsDefaults_AndSaysSo()
    {
        var fake = new FakeStore();
        await fake.SetAsync("options", "this is not json {{{");
        var log = new CapturingLogger();
        var store = new OptionsStore(fake, log);

        var loaded = await store.LoadAsync();

        Assert.Equal(new RenamerOptions(), loaded); // catches JsonException → defaults

        // Defaults are indistinguishable from a correct empty configuration at every layer above this,
        // so this line is the ONLY evidence that a user's stored settings were discarded rather than
        // never written. Asserted at the level and the carried exception and NOT at the wording, which
        // would pin the sentence instead of the behaviour.
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.IsAssignableFrom<JsonException>(entry.Error);
    }

    [Fact]
    public async Task SaveAsync_PersistsSingleBlob_UnderOptionsKey()
    {
        var fake = new FakeStore();
        var store = new OptionsStore(fake);

        await store.SaveAsync(new RenamerOptions { FilenameTemplate = "$title - $studio" });

        var all = await fake.GetAllAsync();
        Assert.Single(all);                       // exactly one entry (single JSON blob)
        Assert.True(all.ContainsKey("options"));  // under the "options" key
    }

    /// <summary>Everything the store logged, so a fallback that says nothing fails here.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, Exception? Error)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, exception));
    }
}
