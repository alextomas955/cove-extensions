using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;

namespace WhisparrSync.Tests.Options;

// The options the settings page has no control for. Each expected default is written out by hand,
// because a value read back off the record under test would agree with it after someone changes it.
public sealed class AdvancedOptionDefaultsTests
{
    private const string RequestHost = "http://cove.internal:5073";

    private const string ExtensionId = "com.alextomas955.whisparrsync";

    [Fact]
    public void TheDefaultMonitorScopeIsFutureScenes()
        => Assert.Equal(MonitorScope.FutureScenes, new WhisparrSyncOptions().DefaultMonitorScope);

    // A blank endpoint means the provider's standard address.
    [Fact]
    public void BothMetadataProviderEndpointsAreBlankByDefault()
    {
        var endpoints = new WhisparrSyncOptions().MetadataProviderEndpoints;

        Assert.Equal("", endpoints.V3);
        Assert.Equal("", endpoints.V2);
    }

    [Fact]
    public void TheCallbackHostIsBlankByDefault()
        => Assert.Equal("", new WhisparrSyncOptions().CallbackHost);

    // Add means a redelivery naming a different file attaches it and touches nothing else.
    [Fact]
    public void TheUpgradeBehaviorDefaultsToAdd()
        => Assert.Equal(UpgradeBehavior.Add, new WhisparrSyncOptions().UpgradeBehavior);

    [Fact]
    public void TheBackstopIntervalDefaultsToFifteenMinutes()
    {
        Assert.Equal(900, new WhisparrSyncOptions().BackstopIntervalSeconds);
        Assert.Equal(TimeSpan.FromMinutes(15), new WhisparrSyncOptions().BackstopInterval);
    }

    [Fact]
    public void TheImportHealthAggregateIsEmptyByDefault()
    {
        var health = new WhisparrSyncOptions().ImportHealth;

        Assert.Null(health.LastWorkedAtUtc);
        Assert.Null(health.LastFailedAtUtc);
        Assert.Equal("", health.LastError);
        Assert.Equal(0, health.ConsecutiveFailures);
        Assert.False(health.BackstopPositionLost);
    }

    [Fact]
    public void ThereAreNoRefusalsByDefault()
        => Assert.Empty(new WhisparrSyncOptions().ImportRefusals);

    // The host comes from the default record, not from a blank literal, so a non-blank default
    // fails here too.
    [Fact]
    public void AnUnsetCallbackHostFallsBackToTheRequestHost()
    {
        var resolved = CallbackAddress.ResolveHost(new WhisparrSyncOptions().CallbackHost, RequestHost);

        Assert.Equal(RequestHost, resolved);
        Assert.Equal(
            RequestHost + CallbackAddress.RouteFor(ExtensionId),
            CallbackAddress.WithoutSecret(resolved, ExtensionId));
    }

    // The page submits no value for any of them. A save that rebuilt the record from the request
    // would return an operator's settings to their defaults and discard the import state with them.
    [Theory]
    [MemberData(nameof(SavesThePageCanSubmit))]
    public void ASettingsSaveLeavesEveryOptionThePageHasNoControlForAlone(
        WhisparrSyncSettingsSaveRequest save)
    {
        var defaults = new WhisparrSyncOptions();
        var configured = defaults with
        {
            DefaultMonitorScope = MonitorScope.AllScenes,
            MetadataProviderEndpoints = new MetadataProviderEndpoints { V3 = "http://provider.invalid/v3" },
            CallbackHost = "http://cove.example:8080",
            UpgradeBehavior = UpgradeBehavior.Replace,
            BackstopIntervalSeconds = 60,
            ImportHealth = new ImportHealthAggregate
            {
                LastWorkedAtUtc = new DateTimeOffset(2026, 8, 30, 9, 0, 0, TimeSpan.Zero),
                ConsecutiveFailures = 2,
                LastError = "the host refused the path",
                BackstopPositionLost = true,
            },
            ImportRefusals =
            [
                new ImportRootRefusals
                {
                    Root = "/whisparr/media",
                    CountSinceLastSuccess = 4,
                    NewestPaths =
                    [
                        new ImportRefusalEntry
                        {
                            Path = "/whisparr/media/scene/file.mp4",
                            Cause = ImportRefusalCause.NotFoundUnderAnyRoot,
                        },
                    ],
                },
            ],
        };

        AssertTheyMatch(defaults, SettingsProjector.Apply(defaults, save));
        AssertTheyMatch(configured, SettingsProjector.Apply(configured, save));
    }

    public static TheoryData<WhisparrSyncSettingsSaveRequest> SavesThePageCanSubmit()
        => new(
            new WhisparrSyncSettingsSaveRequest(WhisparrGeneration.V3, null, null),
            new WhisparrSyncSettingsSaveRequest(
                WhisparrGeneration.V3,
                new WhisparrSyncGenerationSaveRequest("http://whisparr:6969", KeyWriteSignal.Replace, "k"),
                null),
            new WhisparrSyncSettingsSaveRequest(
                WhisparrGeneration.V2,
                null,
                new WhisparrSyncGenerationSaveRequest("http://whisparr-v2:6969", KeyWriteSignal.Clear, null)));

    private static void AssertTheyMatch(WhisparrSyncOptions before, WhisparrSyncOptions after)
    {
        Assert.Equal(before.DefaultMonitorScope, after.DefaultMonitorScope);
        Assert.Equal(before.MetadataProviderEndpoints, after.MetadataProviderEndpoints);
        Assert.Equal(before.CallbackHost, after.CallbackHost);
        Assert.Equal(before.UpgradeBehavior, after.UpgradeBehavior);
        Assert.Equal(before.BackstopIntervalSeconds, after.BackstopIntervalSeconds);
        Assert.Equal(before.ImportHealth, after.ImportHealth);
        Assert.Equal(before.ImportRefusals, after.ImportRefusals);
    }
}
