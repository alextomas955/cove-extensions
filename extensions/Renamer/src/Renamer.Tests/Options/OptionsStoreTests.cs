using Renamer.Options;

namespace Renamer.Tests.Options;

[Trait("Tier", "L0")]
public sealed class OptionsStoreTests
{
    [Fact]
    public async Task LoadAsync_AbsentKey_ReturnsDefaults()
    {
        var store = new OptionsStore(new FakeStore());

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

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

        await store.SaveAsync(custom, TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(custom, loaded);
    }

    [Fact]
    public async Task LoadAsync_CorruptBlob_ReturnsDefaults()
    {
        var fake = new FakeStore();
        await fake.SetAsync("options", "this is not json {{{", TestContext.Current.CancellationToken);
        var store = new OptionsStore(fake);

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new RenamerOptions(), loaded); // catches JsonException → defaults
    }

    [Fact]
    public async Task SaveAsync_PersistsSingleBlob_UnderOptionsKey()
    {
        var fake = new FakeStore();
        var store = new OptionsStore(fake);

        await store.SaveAsync(new RenamerOptions { FilenameTemplate = "$title - $studio" }, TestContext.Current.CancellationToken);

        var all = await fake.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Single(all);                       // exactly one entry (single JSON blob)
        Assert.True(all.ContainsKey("options"));  // under the "options" key
    }
}
