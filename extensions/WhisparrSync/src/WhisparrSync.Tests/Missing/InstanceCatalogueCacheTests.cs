using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Missing;

// What the entry holds is one instance's own row ids. A caller that took them from this cache goes
// on to address a write by them, so an entry answered for another instance would send one
// installation's row number to a different installation.
public sealed class InstanceCatalogueCacheTests
{
    private const string Key = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    private static readonly WhisparrCatalogueScene[] Scenes =
        [new("a-scene", "A scene", null, null, null, null, [], [], false, false, InstanceSceneId: 57)];

    [Fact]
    public void AnotherInstanceAtTheSameGenerationIsAnsweredNothing()
    {
        var cache = new InstanceCatalogueCache(TimeProvider.System);
        cache.Hold(At("http://whisparr-a.invalid:6969"), WhisparrEntityKind.Studio, "a-studio", Scenes);

        Assert.Null(
            cache.Held(At("http://whisparr-b.invalid:6969"), WhisparrEntityKind.Studio, "a-studio"));
    }

    // Two instances behind one reverse proxy differ by their URL base alone, which a stored address
    // keeps.
    [Fact]
    public void AnotherInstanceUnderTheSameAuthorityIsAnsweredNothing()
    {
        var cache = new InstanceCatalogueCache(TimeProvider.System);
        cache.Hold(At("http://whisparr.invalid/one"), WhisparrEntityKind.Studio, "a-studio", Scenes);

        Assert.Null(cache.Held(At("http://whisparr.invalid/two"), WhisparrEntityKind.Studio, "a-studio"));
    }

    // A key rotated against the same address still names the instance whose rows these are, so the
    // entry stands. The key is not part of what identifies an instance.
    [Fact]
    public void TheSameInstanceUnderANewKeyIsAnsweredWhatItHeld()
    {
        var cache = new InstanceCatalogueCache(TimeProvider.System);
        cache.Hold(At("http://whisparr.invalid:6969"), WhisparrEntityKind.Studio, "a-studio", Scenes);

        Assert.Equal(
            Scenes,
            cache.Held(
                At("http://whisparr.invalid:6969", "ffffffffffffffffffffffffffffffff"),
                WhisparrEntityKind.Studio,
                "a-studio"));
    }

    [Fact]
    public void TheOtherGenerationAtTheSameAddressIsAnsweredNothing()
    {
        var cache = new InstanceCatalogueCache(TimeProvider.System);
        cache.Hold(At("http://whisparr.invalid:6969"), WhisparrEntityKind.Studio, "a-studio", Scenes);

        Assert.Null(
            cache.Held(
                At("http://whisparr.invalid:6969", generation: WhisparrGeneration.V2),
                WhisparrEntityKind.Studio,
                "a-studio"));
    }

    private static WhisparrBinding At(
        string address,
        string apiKey = Key,
        WhisparrGeneration generation = WhisparrGeneration.V3)
        => new(generation, new Uri(address), apiKey);
}
