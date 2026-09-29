using Cove.Core.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Missing;

// The address and the key are one row, and that row is the only place either is stored. A page
// binds to the pair it holds, so it cannot present one instance's key to another.
public sealed class MissingOutboundPairTests
{
    private const string MovedAddress = "http://whisparr-elsewhere:6969";
    private const string MovedKey = "1f3f1f3f1f3f1f3f1f3f1f3f1f3f1f3f";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ThePageBindsToTheAddressHeldBesideTheKeyItSends()
    {
        var instances = Recording();

        await ReadPageAsync(MovedRow(), instances);

        var binding = Assert.Single(instances.Bindings);
        Assert.True(ConnectionTester.IsSameAddress(MovedAddress, binding.BaseAddress.ToString()));
        Assert.Equal(MovedKey, binding.ApiKey);
    }

    // A row holding a key and no address names no instance, so nothing is sent rather than a
    // request composed against a default.
    [Fact]
    public async Task ARowCarryingNoAddressReachesNothing()
    {
        var instances = Recording();
        var credentials = new RecordingCredentialPort().Holding(WhisparrGeneration.V3, MovedKey);

        await ReadPageAsync(credentials, instances);

        Assert.Empty(instances.Bindings);
    }

    private static RecordingCredentialPort MovedRow()
        => new RecordingCredentialPort().Holding(WhisparrGeneration.V3, MovedAddress, MovedKey);

    private static FixedInstanceFactory Recording()
        => new(new RecordingWhisparrV3Client(new WhisparrResponse(200, "application/json", "[]")));

    private static async Task ReadPageAsync(
        ICredentialPort credentials, FixedInstanceFactory instances)
    {
        var options = new OptionsStore(new FakeStore());
        await options.SaveAsync(
            new WhisparrSyncOptions { SelectedGeneration = WhisparrGeneration.V3 }.WithConnectionFor(
                WhisparrGeneration.V3,
                new WhisparrSyncGenerationConnection()),
            TestCt);

        await WhisparrSync.ReadMissingPageAsync(
            new EntityRoute("studio", 7),
            new MissingNarrowing(1, 40, null, null, null, null),
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead),
            new WhisparrAccess(options, credentials, instances, NullLogger.Instance),
            new ProviderEndpointPort(null),
            new MissingPagePlanner(
                new MissingIdentityResolver(
                    new StubEntityIdentities("a-studio"),
                    TestProviderCatalogues.Naming(new StubProviderCatalogue([])),
                    new StubEntityNames()),
                TestProviderCatalogues.Naming(new StubProviderCatalogue([])),
                new StubOwnedScenes(),
                new InstanceCatalogueCache(TimeProvider.System)),
            TestCt);
    }
}
