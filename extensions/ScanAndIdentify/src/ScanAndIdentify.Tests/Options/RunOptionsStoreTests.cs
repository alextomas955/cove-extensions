using ScanAndIdentify.Options;

namespace ScanAndIdentify.Tests.Options;

public sealed class RunOptionsStoreTests
{
    [Fact]
    public async Task Load_AnswersTheRecommendedDefault_WhenNoOptionsKeyIsStored()
    {
        var store = new RunOptionsStore(new FakeStore());

        var options = await store.LoadAsync();

        Assert.True(options.BackfillFingerprints);
    }

    [Fact]
    public async Task Load_AnswersTheStoredValue_WhenTheBlobTurnsTheDefaultOff()
    {
        var fake = new FakeStore();
        await fake.SetAsync(RunOptionsStore.Key, """{"BackfillFingerprints":false}""");
        var store = new RunOptionsStore(fake);

        var options = await store.LoadAsync();

        Assert.False(options.BackfillFingerprints);
    }

    [Fact]
    public async Task Save_WritesOneKeyHoldingThePascalCaseMemberName()
    {
        var fake = new FakeStore();
        var store = new RunOptionsStore(fake);

        await store.SaveAsync(new RunOptions { BackfillFingerprints = false });

        Assert.Equal(1, fake.SetCallCount);
        Assert.Equal([RunOptionsStore.Key], (await fake.GetAllAsync()).Keys);
        var stored = await fake.GetAsync(RunOptionsStore.Key);
        Assert.Contains("\"BackfillFingerprints\"", stored, StringComparison.Ordinal);
    }
}
