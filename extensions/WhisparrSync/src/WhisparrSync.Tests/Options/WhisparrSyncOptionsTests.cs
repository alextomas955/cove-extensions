using WhisparrSync.Contracts;
using WhisparrSync.Options;

namespace WhisparrSync.Tests.Options;

/// <summary>
/// The options blob: what a default carries, that a round-trip through the store returns an equal
/// record, and that the two generations' connections are independent of one another.
/// </summary>
public sealed class WhisparrSyncOptionsTests
{
    /// <summary>
    /// The four advanced options have no control in the settings page, so their defaults are the
    /// values every install runs on. A change to one of these is a behaviour change for everybody.
    /// </summary>
    [Fact]
    public void TheAdvancedOptionsCarryTheirStatedDefaults()
    {
        var options = new WhisparrSyncOptions();

        Assert.Empty(options.PathTranslation);
        Assert.Equal(MonitorScope.NewReleasesOnly, options.DefaultMonitorScope);
        Assert.Equal("", options.MetadataProviderEndpoints.V3);
        Assert.Equal("", options.MetadataProviderEndpoints.V2);
        Assert.Equal("", options.CallbackHost);
    }

    /// <summary>Neither generation is configured until something configures it.</summary>
    [Fact]
    public void ADefaultRecordHasNoConnectionForEitherGeneration()
    {
        var options = new WhisparrSyncOptions();

        Assert.Null(options.V3);
        Assert.Null(options.V2);
    }

    /// <summary>
    /// A save followed by a load returns an equal record, including the one collection member. Record
    /// equality compares a list by reference, so a round-trip's fresh list is exactly the case a
    /// default implementation would report as changed.
    /// </summary>
    [Fact]
    public async Task ARoundTripThroughTheStoreReturnsAnEqualRecord()
    {
        var store = new FakeStore();
        var saved = Populated();

        await new OptionsStore(store).SaveAsync(saved);
        var loaded = await new OptionsStore(store).LoadAsync();

        Assert.Equal(saved, loaded);
        Assert.Equal(saved.GetHashCode(), loaded.GetHashCode());
        Assert.Equal(2, loaded.PathTranslation.Count);
    }

    /// <summary>
    /// The enums survive the round trip as their own spelling rather than as an ordinal, which is
    /// what keeps a stored blob readable after a member is inserted into either enum.
    /// </summary>
    [Fact]
    public async Task TheEnumsRoundTripAsStrings()
    {
        var store = new FakeStore();

        await new OptionsStore(store).SaveAsync(
            Populated() with { SelectedGeneration = WhisparrGeneration.V2, DefaultMonitorScope = MonitorScope.AllScenes });

        var blob = await store.GetAsync(OptionsStore.Key);
        Assert.Contains("\"v2\"", blob, StringComparison.Ordinal);
        Assert.Contains("\"allScenes\"", blob, StringComparison.Ordinal);
    }

    /// <summary>
    /// Writing one generation's connection leaves the other's at the value it already had. Selecting
    /// the other generation and coming back has to return the first one unchanged.
    /// </summary>
    [Fact]
    public async Task WritingOneGenerationLeavesTheOtherUntouched()
    {
        var store = new FakeStore();
        var options = new OptionsStore(store);
        await options.SaveAsync(Populated());

        var reloaded = await options.LoadAsync();
        await options.SaveAsync(reloaded with
        {
            V3 = reloaded.V3! with { Address = "http://moved:6969/", RecordedVersion = "3.3.9.1" },
        });

        var after = await options.LoadAsync();
        Assert.Equal("http://v2-host:6969/", after.V2?.Address);
        Assert.Equal("2.2.0.231", after.V2?.RecordedVersion);
        Assert.Equal("http://moved:6969/", after.V3?.Address);
    }

    /// <summary>
    /// A generation nothing configured reads as absent. It must never read back the other
    /// generation's address, which would send a test at an instance the user did not name.
    /// </summary>
    [Fact]
    public async Task AGenerationNeverConfiguredReadsAsAbsent()
    {
        var store = new FakeStore();
        var options = new OptionsStore(store);

        await options.SaveAsync(new WhisparrSyncOptions
        {
            V3 = new WhisparrSyncGenerationConnection { Address = "http://v3-host:6969/" },
        });

        var loaded = await options.LoadAsync();
        Assert.Null(loaded.V2);
        Assert.Equal("http://v3-host:6969/", loaded.V3?.Address);
    }

    /// <summary>
    /// The two recorded instants are stored apart, because they measure different things: when the
    /// version was read, and when the instance last answered anything at all.
    /// </summary>
    [Fact]
    public async Task TheTwoRecordedInstantsSurviveIndependently()
    {
        var store = new FakeStore();
        var options = new OptionsStore(store);
        await options.SaveAsync(Populated());

        var loaded = await options.LoadAsync();

        Assert.Equal(VerifiedAt, loaded.V3?.VersionVerifiedAtUtc);
        Assert.Equal(ReachableAt, loaded.V3?.LastReachableAtUtc);
    }

    /// <summary>A blob nothing ever wrote loads as the defaults rather than throwing.</summary>
    [Fact]
    public async Task AnEmptyStoreLoadsTheDefaults()
        => Assert.Equal(new WhisparrSyncOptions(), await new OptionsStore(new FakeStore()).LoadAsync());

    /// <summary>
    /// The API key has no home in this record. It is in a table this extension owns, so the host's
    /// bulk extension-data route has nothing of it to return.
    /// </summary>
    [Fact]
    public async Task TheStoredBlobCarriesNothingNamedLikeAKey()
    {
        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(Populated());

        var blob = await store.GetAsync(OptionsStore.Key);
        Assert.DoesNotContain("key", blob, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly DateTimeOffset VerifiedAt = new(2026, 8, 30, 11, 22, 33, TimeSpan.Zero);
    private static readonly DateTimeOffset ReachableAt = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static WhisparrSyncOptions Populated() => new()
    {
        SelectedGeneration = WhisparrGeneration.V3,
        V3 = new WhisparrSyncGenerationConnection
        {
            Address = "http://v3-host:6969/",
            RecordedVersion = "3.3.8.1097",
            VersionVerifiedAtUtc = VerifiedAt,
            LastReachableAtUtc = ReachableAt,
        },
        V2 = new WhisparrSyncGenerationConnection
        {
            Address = "http://v2-host:6969/",
            RecordedVersion = "2.2.0.231",
            VersionVerifiedAtUtc = VerifiedAt,
            LastReachableAtUtc = ReachableAt,
        },
        PathTranslation =
        [
            new PathTranslationRule { CovePrefix = "/media", WhisparrPrefix = "/data" },
            new PathTranslationRule { CovePrefix = "/archive", WhisparrPrefix = "/old" },
        ],
        MetadataProviderEndpoints = new MetadataProviderEndpoints { V3 = "http://provider.invalid/v3" },
        CallbackHost = "https://media.example.com/cove",
    };
}
