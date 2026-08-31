using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;

namespace WhisparrSync.Tests.Options;

/// <summary>
/// Everything the settings page has no control for: the values every install runs on, and what the
/// shipped code does while they stay at them.
/// </summary>
/// <remarks>
/// Two groups. The three advanced options the product specification lists as unexposed - the default
/// monitor scope, the metadata provider endpoints and the callback host - have their expected
/// defaults transcribed by hand from that table. A value read back off the record under test would
/// agree with that record forever, including after someone changes it.
/// <para>
/// The second group is the import state: the upgrade behavior, the backstop interval, the import
/// health aggregate and the per-root refusals. The page submits nothing for either group, so what a
/// save must do to both is the same, and both are asserted through the one projection below.
/// </para>
/// <para>
/// What is asserted about behaviour is what a shipped code path decides. Only the callback host and
/// the backstop interval have a consumer today, so for the rest the only thing that can be true of
/// them is that a save through the page leaves them alone, which is what the page having no control
/// for them has to mean.
/// </para>
/// </remarks>
public sealed class AdvancedOptionDefaultsTests
{
    /// <summary>The host a request would arrive on, for the callback-host fallback below.</summary>
    private const string RequestHost = "http://cove.internal:5073";

    private const string ExtensionId = "com.alextomas955.whisparrsync";

    [Fact]
    public void TheDefaultMonitorScopeIsNewReleasesOnly()
        => Assert.Equal(MonitorScope.NewReleasesOnly, new WhisparrSyncOptions().DefaultMonitorScope);

    /// <summary>
    /// Both metadata provider endpoints are blank by default, which means each provider's standard
    /// address.
    /// </summary>
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

    /// <summary>
    /// A redelivery naming a different file attaches it and touches nothing else, until someone
    /// chooses otherwise.
    /// </summary>
    [Fact]
    public void TheUpgradeBehaviorDefaultsToAdd()
        => Assert.Equal(UpgradeBehavior.Add, new WhisparrSyncOptions().UpgradeBehavior);

    /// <summary>The backstop runs every fifteen minutes until something changes it.</summary>
    [Fact]
    public void TheBackstopIntervalDefaultsToFifteenMinutes()
    {
        Assert.Equal(900, new WhisparrSyncOptions().BackstopIntervalSeconds);
        Assert.Equal(TimeSpan.FromMinutes(15), new WhisparrSyncOptions().BackstopInterval);
    }

    /// <summary>An install where nothing has run yet reports no health of any kind.</summary>
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

    /// <summary>
    /// A blank callback host builds the callback address on the host the request arrived on.
    /// </summary>
    /// <remarks>
    /// Taken from the default record rather than from a blank literal, so changing the default to
    /// anything non-blank fails here as well as above.
    /// </remarks>
    [Fact]
    public void AnUnsetCallbackHostFallsBackToTheRequestHost()
    {
        var resolved = CallbackAddress.ResolveHost(new WhisparrSyncOptions().CallbackHost, RequestHost);

        Assert.Equal(RequestHost, resolved);
        Assert.Equal(
            RequestHost + CallbackAddress.RouteFor(ExtensionId),
            CallbackAddress.WithoutSecret(resolved, ExtensionId));
    }

    /// <summary>
    /// A settings save leaves every one of them exactly as it was.
    /// </summary>
    /// <remarks>
    /// The settings page submits no value for any of them, so a save that rebuilt the record from the
    /// request would silently return an operator's settings to their defaults and discard the import
    /// state with them. Both directions are asserted: an install that never set one keeps the
    /// default, and one that set every one keeps those values.
    /// </remarks>
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
