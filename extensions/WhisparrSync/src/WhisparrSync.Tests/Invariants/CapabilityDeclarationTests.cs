using WhisparrSync.Contracts;
using WhisparrSync.Scene;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Invariants;

// The capability enum is on the wire and the per-generation arrays are what the settings page and
// the monitor menu render from, so they survive as a declaration. Nothing at run time reads them to
// decide what a caller may do: a caller tests the bound instance for the role interface it needs.
//
// That leaves one thing the compiler cannot catch. A capability named in an array whose instance
// implements no matching role would offer a control that always refuses, and a role an instance
// implements that its array does not name would hide a control that works. This states the tie
// between the two in both directions.
public sealed class CapabilityDeclarationTests
{
    // Transcribed by hand, one row per capability, beside the role interface that expresses it. The
    // removed runtime table was the only thing tying the two together; this is that fact written
    // down where a change to either side has to answer for it.
    private static readonly Dictionary<WhisparrCapability, Type> RoleByCapability = new()
    {
        [WhisparrCapability.OutOfBandCallbackSecret] = typeof(IOutOfBandSecretRegistration),
        [WhisparrCapability.MonitorStudio] = typeof(IWhisparrStudioActing),
        [WhisparrCapability.MonitorPerformer] = typeof(IWhisparrPerformerActing),
        [WhisparrCapability.RegisterMissingScenes] = typeof(IWhisparrMissingSceneActing),
        [WhisparrCapability.RegisterOwnedSites] = typeof(IWhisparrSiteRegistrationActing),
        [WhisparrCapability.ReflectOwnedFiles] = typeof(IWhisparrReflectOwnedActing),
        [WhisparrCapability.SearchMonitored] = typeof(IWhisparrSearchGrabbing),
        [WhisparrCapability.SearchScene] = typeof(IWhisparrSceneSearchGrabbing),
        [WhisparrCapability.ReadSceneStatus] = typeof(IWhisparrSceneStatusReading),
        [WhisparrCapability.ReadSceneExclusions] = typeof(IWhisparrSceneExclusionReading),
        [WhisparrCapability.MonitorScene] = typeof(IWhisparrSceneMonitorActing),
        [WhisparrCapability.ExcludeScene] = typeof(IWhisparrSceneExclusionActing),
        [WhisparrCapability.ReadSiteSceneRows] = typeof(IWhisparrSiteSceneReading),
        [WhisparrCapability.ReadHeldSites] = typeof(IWhisparrHeldSiteReading),
        [WhisparrCapability.ReadEntityCardsInBatch] = typeof(IWhisparrEntityBatchReading),
        [WhisparrCapability.ReadSceneCardsInBatch] = typeof(IWhisparrSceneBatchReading),
        [WhisparrCapability.TrackEntityCatalogue] = typeof(IWhisparrEntityTrackingActing),
        [WhisparrCapability.ReadEntityCatalogue] = typeof(IWhisparrEntityCatalogueReading),
        [WhisparrCapability.ReadInstanceFilesystem] = typeof(IWhisparrInstanceFilesystemReading),
    };

    [Theory]
    [InlineData(WhisparrGeneration.V3, typeof(WhisparrV3Instance))]
    [InlineData(WhisparrGeneration.V2, typeof(WhisparrV2Instance))]
    public void AGenerationDeclaresExactlyTheCapabilitiesItsInstanceImplements(
        WhisparrGeneration generation, Type instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var implemented = instance.GetInterfaces();

        Assert.Equal(
            RoleByCapability
                .Where(row => implemented.Contains(row.Value))
                .Select(row => row.Key)
                .Order(),
            GenerationCapabilities.CapabilitiesOf(generation).Order());
    }

    // Every capability on the wire expresses a role, so a new one cannot be added to the enum and
    // rendered in a menu with nothing behind it.
    [Fact]
    public void EveryCapabilityNamesARole()
        => Assert.Equal(
            Enum.GetValues<WhisparrCapability>().Order(),
            RoleByCapability.Keys.Order());

    // Whether a wider scope rewrites what is already monitored is declared beside the arrays and is
    // answered to the browser on the monitoring view. A generation left out of the declaration
    // throws where it is read, which is a monitoring read failing rather than a menu quietly
    // dropping the warning a reader sees before a back catalogue is marked wanted.
    [Fact]
    public void EveryGenerationDeclaresWhetherAScopeChangeIsRetroactive()
        => Assert.Equal(
            Enum.GetValues<WhisparrGeneration>().Order(),
            GenerationCapabilities.GenerationsDeclaringScopeBehaviour.Order());
}
