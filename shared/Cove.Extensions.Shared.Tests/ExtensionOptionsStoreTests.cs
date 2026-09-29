using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Extensions.Shared.Tests;

public sealed class ExtensionOptionsStoreTests
{
    private static readonly JsonSerializerOptions Json = new();

    // A factory value that differs from the model's own initializers, so a result that came from the
    // factory is distinguishable from one a deserializer built.
    private static ToyOptions FromFactory() => new() { Name = "from-factory" };

    private static ExtensionOptionsStore<ToyOptions> Store(
        FakeStore fake,
        ILogger? logger = null,
        Func<ToyOptions, ToyOptions>? normalize = null)
        => new(fake, Json, FromFactory, logger ?? NullLogger.Instance, normalize);

    private static string Canonical(ToyOptions options) => JsonSerializer.Serialize(options, Json);

    private static async Task<FakeStore> Holding(string blob)
    {
        var fake = new FakeStore();
        await fake.SetAsync(ExtensionOptionsStore<ToyOptions>.Key, blob);
        return fake;
    }

    [Fact]
    public async Task LoadAsync_AbsentKey_ReturnsTheFactoryValue()
    {
        var loaded = await Store(new FakeStore()).LoadAsync();

        Assert.Equal(Canonical(FromFactory()), Canonical(loaded));
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsEveryMember()
    {
        var fake = new FakeStore();
        var store = Store(fake);
        var custom = new ToyOptions
        {
            Name = "custom",
            Items = ["only"],
            Nested = new ToyNested { Label = "custom-label", Ids = [1, 2] },
            Note = null,
            Count = 9,
        };

        await store.SaveAsync(custom);
        var loaded = await store.LoadAsync();

        Assert.Equal(Canonical(custom), Canonical(loaded));
    }

    [Fact]
    public async Task SaveAsync_PersistsOneBlob_UnderTheOptionsKey()
    {
        var fake = new FakeStore();

        await Store(fake).SaveAsync(new ToyOptions { Name = "saved" });

        var all = await fake.GetAllAsync();
        var blob = Assert.Single(all);
        Assert.Equal("options", blob.Key);
        Assert.Equal("saved", JsonSerializer.Deserialize<ToyOptions>(blob.Value, Json)!.Name);
    }

    [Fact]
    public async Task LoadAsync_CorruptBlob_ReturnsTheFactoryValue_AndLogsOneWarning()
    {
        var log = new CapturingLogger<ToyOptions>();

        var loaded = await Store(await Holding("this is not json {{{"), log).LoadAsync();

        Assert.Equal(Canonical(FromFactory()), Canonical(loaded));

        // The warning is the only sign that stored settings were discarded rather than never written.
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.IsType<JsonException>(entry.Error, exactMatch: false);
    }

    [Fact]
    public async Task LoadAsync_NonNullableMembersStoredAsNull_TakeTheFactoryDefault_AndTheRestStaysAsStored()
    {
        // A property initializer runs only for an absent key, so an explicit null binds to null and the
        // member contradicts its own non-nullable declaration.
        var fake = await Holding("""{"Name":null,"Items":null,"Nested":null,"Count":5}""");

        var loaded = await Store(fake).LoadAsync();

        Assert.Equal("from-factory", loaded.Name);
        Assert.Equal(["first", "second"], loaded.Items);
        Assert.Equal(JsonSerializer.Serialize(new ToyNested(), Json), JsonSerializer.Serialize(loaded.Nested, Json));
        Assert.Equal(5, loaded.Count);
    }

    [Fact]
    public async Task LoadAsync_NestedMemberStoredAsNull_TakesItsDefault_AndItsSiblingStaysAsStored()
    {
        var fake = await Holding("""{"Nested":{"Label":"stored-label","Ids":null}}""");

        var loaded = await Store(fake).LoadAsync();

        Assert.Equal([7], loaded.Nested.Ids);
        Assert.Equal("stored-label", loaded.Nested.Label);
    }

    [Fact]
    public async Task LoadAsync_NullableMemberStoredAsNull_StaysNull()
    {
        // The restore is keyed on the declared nullability rather than on whether a default exists, so
        // a member whose null is a real state keeps it rather than gaining a value the user cleared.
        var fake = await Holding("""{"Note":null}""");

        var loaded = await Store(fake).LoadAsync();

        Assert.Null(loaded.Note);
    }

    [Fact]
    public async Task LoadAsync_Normalize_SeesTheRestoredMembers()
    {
        // A normalizer that dereferences a member would throw on the raw bind, so it has to run after
        // the restore.
        var fake = await Holding("""{"Items":null}""");

        var loaded = await Store(fake, normalize: o => o with { Count = o.Items.Count }).LoadAsync();

        Assert.Equal(2, loaded.Count);
    }
}
