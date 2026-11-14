using ScanAndIdentify.Options;

namespace ScanAndIdentify.Tests.Options;

public sealed class RunOptionsStoreTests
{
    // Every member set to the opposite of its default, in this record's own stored spelling, so a
    // renamed member fails the test rather than quietly stopping to bind.
    private const string AllFlipped = """
        {
          "Thumbnails": false,
          "Previews": false,
          "Sprites": false,
          "VrStereo": true,
          "ImageThumbnails": false,
          "GalleryThumbnails": false,
          "BackfillFingerprints": false,
          "Rescan": true,
          "CreateMissingEntities": false,
          "MarkOrganized": true
        }
        """;

    [Fact]
    public void Defaults_AreTheRecommendedSet()
    {
        var options = new RunOptions();

        Assert.True(options.Thumbnails);
        Assert.True(options.Previews);
        Assert.True(options.Sprites);
        Assert.False(options.VrStereo);
        Assert.True(options.ImageThumbnails);
        Assert.True(options.GalleryThumbnails);
        Assert.True(options.BackfillFingerprints);
        Assert.False(options.Rescan);
        Assert.True(options.CreateMissingEntities);
        Assert.False(options.MarkOrganized);
    }

    [Fact]
    public async Task Load_AnswersTheRecommendedDefaults_WhenNoOptionsKeyIsStored()
    {
        var store = new RunOptionsStore(new FakeStore());

        var options = await store.LoadAsync();

        Assert.Equal(new RunOptions(), options);
        Assert.True(options.BackfillFingerprints);
        Assert.False(options.VrStereo);
    }

    [Fact]
    public async Task Load_AnswersEveryStoredValue_WhenTheBlobFlipsThemAll()
    {
        var fake = new FakeStore();
        await fake.SetAsync(RunOptionsStore.Key, AllFlipped);
        var store = new RunOptionsStore(fake);

        var options = await store.LoadAsync();

        Assert.False(options.Thumbnails);
        Assert.False(options.Previews);
        Assert.False(options.Sprites);
        Assert.True(options.VrStereo);
        Assert.False(options.ImageThumbnails);
        Assert.False(options.GalleryThumbnails);
        Assert.False(options.BackfillFingerprints);
        Assert.True(options.Rescan);
        Assert.False(options.CreateMissingEntities);
        Assert.True(options.MarkOrganized);
    }

    [Fact]
    public async Task Load_KeepsTheDeclaredFields_WhenTheBlobCarriesAKeyTheRecordDoesNotDeclare()
    {
        var fake = new FakeStore();
        await fake.SetAsync(
            RunOptionsStore.Key,
            """{"BackfillFingerprints":false,"SomethingANewerVersionStored":true}""");
        var store = new RunOptionsStore(fake);

        var options = await store.LoadAsync();

        Assert.False(options.BackfillFingerprints);
        Assert.True(options.Thumbnails);
    }

    [Fact]
    public async Task Save_WritesOneKeyHoldingThePascalCaseMemberNames()
    {
        var fake = new FakeStore();
        var store = new RunOptionsStore(fake);

        await store.SaveAsync(new RunOptions { BackfillFingerprints = false });

        Assert.Equal(1, fake.SetCallCount);
        Assert.Equal([RunOptionsStore.Key], (await fake.GetAllAsync()).Keys);
        var stored = await fake.GetAsync(RunOptionsStore.Key);
        Assert.Contains("\"BackfillFingerprints\"", stored, StringComparison.Ordinal);
        Assert.Contains("\"GalleryThumbnails\"", stored, StringComparison.Ordinal);
    }
}
