using WhisparrSync.Contracts;
using WhisparrSync.Options;

namespace WhisparrSync.Tests.Options;

// A folder answer belongs to the instance that gave it. These drive the real store, so what is
// asserted is what a running Cove reads back after the selection moves.
public sealed class PerGenerationInstanceSettingsTests
{
    private static readonly OutboundRootMapping V3Mapping =
        new() { CoveRoot = "I:\\Downloads\\P", InstanceRoot = "/library" };

    private static readonly OutboundRootMapping V2Mapping =
        new() { CoveRoot = "I:\\Downloads\\P", InstanceRoot = "/i-downloads-p" };

    [Fact]
    public async Task AFolderSettledOnOneGenerationIsNotSettledOnTheOther()
    {
        var options = await StoredAsync(
            new WhisparrSyncOptions { SelectedGeneration = WhisparrGeneration.V3 }
                .WithInstance(outboundMappings: [V3Mapping]));

        var loaded = await options.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("/library", Assert.Single(loaded.Instance(WhisparrGeneration.V3).OutboundMappings).InstanceRoot);
        Assert.Empty(loaded.Instance(WhisparrGeneration.V2).OutboundMappings);
    }

    // The point of the whole arrangement: switching and switching back returns the first answer
    // rather than re-probing for it.
    [Fact]
    public async Task SwitchingAwayAndBackReturnsEachGenerationsOwnAnswer()
    {
        var store = new FakeStore();
        var options = new OptionsStore(store);

        var onV3 = new WhisparrSyncOptions { SelectedGeneration = WhisparrGeneration.V3 }
            .WithInstance(outboundMappings: [V3Mapping]);
        await options.SaveAsync(onV3, TestContext.Current.CancellationToken);

        var switched = (await options.LoadAsync(TestContext.Current.CancellationToken))
            with
        { SelectedGeneration = WhisparrGeneration.V2 };
        await options.SaveAsync(
            switched.WithInstance(outboundMappings: [V2Mapping]),
            TestContext.Current.CancellationToken);

        var onV2 = await options.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("/i-downloads-p", Assert.Single(onV2.Instance().OutboundMappings).InstanceRoot);

        await options.SaveAsync(
            onV2 with { SelectedGeneration = WhisparrGeneration.V3 },
            TestContext.Current.CancellationToken);

        var backOnV3 = await options.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("/library", Assert.Single(backOnV3.Instance().OutboundMappings).InstanceRoot);
        Assert.Equal("/i-downloads-p", Assert.Single(backOnV3.Instance(WhisparrGeneration.V2).OutboundMappings).InstanceRoot);
    }

    // A generation nothing has asked about is not the same as one whose folders all resolved. The
    // folder section reads the difference to decide whether it has anything to report.
    [Fact]
    public async Task AGenerationThatHasNeverAnsweredIsAbsentRatherThanEmpty()
    {
        var options = await StoredAsync(
            new WhisparrSyncOptions { SelectedGeneration = WhisparrGeneration.V3 }
                .WithInstance(outboundMappings: [V3Mapping]));

        var loaded = await options.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(loaded.InstanceSettingsFor(WhisparrGeneration.V3));
        Assert.Null(loaded.InstanceSettingsFor(WhisparrGeneration.V2));
    }

    // Import refusals name a Whisparr root, and the two instances declare different ones.
    [Fact]
    public async Task AnImportRefusalRecordedOnOneGenerationDoesNotReachTheOther()
    {
        var refusal = new ImportRootRefusals
        {
            Root = "/i-downloads-p",
            CountSinceLastSuccess = 1,
            NewestPaths =
            [
                new ImportRefusalEntry
                {
                    Path = "/i-downloads-p/a.mp4",
                    Cause = ImportRefusalCause.NotFoundUnderAnyRoot,
                },
            ],
        };

        var options = await StoredAsync(
            new WhisparrSyncOptions { SelectedGeneration = WhisparrGeneration.V2 }
                .WithInstance(importRefusals: [refusal]));

        var loaded = await options.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Single(loaded.Instance(WhisparrGeneration.V2).ImportRefusals);
        Assert.Empty(loaded.Instance(WhisparrGeneration.V3).ImportRefusals);
    }

    private static async Task<OptionsStore> StoredAsync(WhisparrSyncOptions options)
    {
        var store = new OptionsStore(new FakeStore());
        await store.SaveAsync(options, TestContext.Current.CancellationToken);
        return store;
    }
}
