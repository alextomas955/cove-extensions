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
