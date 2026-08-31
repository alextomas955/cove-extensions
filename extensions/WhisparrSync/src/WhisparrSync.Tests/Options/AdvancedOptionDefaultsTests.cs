using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Options;

namespace WhisparrSync.Tests.Options;

/// <summary>
/// The four options that are settable and have no control in the settings page: the values every
/// install runs on, and what the shipped code does while they stay at them.
/// </summary>
/// <remarks>
/// Each expected default is transcribed by hand from the product specification's own table. A value
/// read back off the record under test would agree with that record forever, including after someone
/// changes it.
/// <para>
/// What is asserted about behaviour is what a shipped code path decides. Only one of the four has a
/// consumer today: the callback host. The path translation table, the default monitor scope and the
/// metadata provider endpoints are stored and read by nothing yet, so the only thing that can be true
/// of them is that a save through the page leaves them alone, which is what the page having no
/// control for them has to mean.
/// </para>
/// </remarks>
public sealed class AdvancedOptionDefaultsTests
{
    /// <summary>The host a request would arrive on, for the callback-host fallback below.</summary>
    private const string RequestHost = "http://cove.internal:5073";

    private const string ExtensionId = "com.alextomas955.whisparrsync";

    [Fact]
    public void ThePathTranslationTableIsEmptyByDefault()
        => Assert.Empty(new WhisparrSyncOptions().PathTranslation);

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
    /// A settings save leaves all four exactly as they were.
    /// </summary>
    /// <remarks>
    /// The settings page submits no value for any of them, so a save that rebuilt the record from the
    /// request would silently return an operator's settings to their defaults. Both directions are
    /// asserted: an install that never set one keeps the default, and one that set every one keeps
    /// those values.
    /// </remarks>
    [Theory]
    [MemberData(nameof(SavesThePageCanSubmit))]
    public void ASettingsSaveLeavesTheFourUnexposedOptionsAlone(WhisparrSyncSettingsSaveRequest save)
    {
        var defaults = new WhisparrSyncOptions();
        var configured = defaults with
        {
            PathTranslation = [new PathTranslationRule { CovePrefix = "/media", WhisparrPrefix = "/data" }],
            DefaultMonitorScope = MonitorScope.AllScenes,
            MetadataProviderEndpoints = new MetadataProviderEndpoints { V3 = "http://provider.invalid/v3" },
            CallbackHost = "http://cove.example:8080",
        };

        AssertUnexposedOptionsMatch(defaults, SettingsProjector.Apply(defaults, save));
        AssertUnexposedOptionsMatch(configured, SettingsProjector.Apply(configured, save));
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

    private static void AssertUnexposedOptionsMatch(
        WhisparrSyncOptions before, WhisparrSyncOptions after)
    {
        Assert.Equal(before.PathTranslation, after.PathTranslation);
        Assert.Equal(before.DefaultMonitorScope, after.DefaultMonitorScope);
        Assert.Equal(before.MetadataProviderEndpoints, after.MetadataProviderEndpoints);
        Assert.Equal(before.CallbackHost, after.CallbackHost);
    }
}
