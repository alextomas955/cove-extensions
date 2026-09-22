using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Import;

// The address and the key are two writes in two stores. A root read running while a save moves both
// can read one from either side of that save, and the key of the instance being moved to would then
// be presented to the instance being moved from.
public sealed class ReportedRootOutboundPairTests
{
    private const string StoredAddress = "http://whisparr:6969";
    private const string MovedAddress = "http://whisparr-elsewhere:6969";
    private const string MovedKey = "2a4a2a4a2a4a2a4a2a4a2a4a2a4a2a4a";
    private const string V2Address = "http://whisparr-v2:6969";
    private const string V2Key = "5b6b5b6b5b6b5b6b5b6b5b6b5b6b5b6b";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheRootReadBindsToTheAddressHeldBesideTheKeyItSends()
    {
        var instances = Recording();
        var credentials = new RecordingCredentialPort()
            .Holding(WhisparrGeneration.V3, MovedAddress, MovedKey);

        await ReadRootsAsync(credentials, instances, WhisparrGeneration.V3);

        var binding = Assert.Single(instances.Bindings);
        Assert.True(ConnectionTester.IsSameAddress(MovedAddress, binding.BaseAddress.ToString()));
        Assert.Equal(MovedKey, binding.ApiKey);
    }

    // Control for the case above: a read taking the address from the row would satisfy it while a
    // row that carries none reached nothing at all, which is every installation saved before the
    // address was stored there.
    [Fact]
    public async Task ARowCarryingNoAddressStillBindsToTheStoredOne()
    {
        var instances = Recording();
        var credentials = new RecordingCredentialPort().Holding(WhisparrGeneration.V3, MovedKey);

        await ReadRootsAsync(credentials, instances, WhisparrGeneration.V3);

        var binding = Assert.Single(instances.Bindings);
        Assert.True(ConnectionTester.IsSameAddress(StoredAddress, binding.BaseAddress.ToString()));
    }

    // This port answers for the generation it is handed, which a delivery names and the settings do
    // not. A resolution reading the selected generation instead would bind to the other instance.
    [Fact]
    public async Task TheGenerationAskedForIsTheOneBound()
    {
        var instances = Recording();
        var credentials = new RecordingCredentialPort()
            .Holding(WhisparrGeneration.V3, MovedAddress, MovedKey)
            .Holding(WhisparrGeneration.V2, V2Address, V2Key);

        await ReadRootsAsync(credentials, instances, WhisparrGeneration.V2);

        var binding = Assert.Single(instances.Bindings);
        Assert.Equal(WhisparrGeneration.V2, binding.Generation);
        Assert.True(ConnectionTester.IsSameAddress(V2Address, binding.BaseAddress.ToString()));
        Assert.Equal(V2Key, binding.ApiKey);
    }

    private static FixedInstanceFactory Recording()
        => new(new RecordingWhisparrV3Client(new WhisparrResponse(200, "application/json", "[]")));

    // Selected is v3 throughout, and the stored blob names one address for both generations, so a
    // case that switched generation cannot pass on the blob alone.
    private static async Task ReadRootsAsync(
        ICredentialPort credentials, FixedInstanceFactory instances, WhisparrGeneration generation)
    {
        var options = new OptionsStore(new FakeStore());
        await options.SaveAsync(
            new WhisparrSyncOptions { SelectedGeneration = WhisparrGeneration.V3 }
                .WithConnectionFor(
                    WhisparrGeneration.V3,
                    new WhisparrSyncGenerationConnection { Address = StoredAddress })
                .WithConnectionFor(
                    WhisparrGeneration.V2,
                    new WhisparrSyncGenerationConnection { Address = StoredAddress }),
            TestCt);

        await new ReportedRootPort(
                instances,
                options,
                credentials,
                new ReportedRootCache(TimeProvider.System),
                NullLogger.Instance)
            .ReadAsync(generation, TestCt);
    }
}
