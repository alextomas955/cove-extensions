using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

// One file for both generations, because the claim is the same on each: the entity the generation
// registers is registered at the folder this product built for it, with the root beside it. The two
// register different entities, so each arm names its own.
public sealed class RegisteredAtItsOwnFolderTests
{
    private const string InstanceRoot = "/config/library";

    private const string EntityFolder = InstanceRoot + "/.wsync-v3/tt1234567";

    private const string SceneForeignId = "9f0d6f27-1f3a-4a5f-8b21-6b2d3a5f9c10";

    private const int SiteNumber = 3372;

    private static readonly AddDefaults AtItsOwnFolder =
        new(4, InstanceRoot, EntityFolder);

    private static readonly AddDefaults AtTheRootAlone = new(4, InstanceRoot);

    // The instance derives the root from the path, so the two have to agree; the root is sent
    // beside the path because it states which of the instance's roots this product meant.
    [Fact]
    public void TheSceneAddCarriesTheFolderAndTheRootTogether()
    {
        var body = ComposedBody.Of(V3BodyProjector.AddScene(SceneForeignId, AtItsOwnFolder));

        Assert.Equal(EntityFolder, body["path"]!.GetValue<string>());
        Assert.Equal(InstanceRoot, body["rootFolderPath"]!.GetValue<string>());
    }

    [Fact]
    public void TheSiteAddCarriesTheFolderAndTheRootTogether()
    {
        var body = ComposedV2Body.Of(V2BodyProjector.RegisterSite(SiteNumber, AtItsOwnFolder));

        Assert.Equal(EntityFolder, body["path"]!.GetValue<string>());
        Assert.Equal(InstanceRoot, body["rootFolderPath"]!.GetValue<string>());
    }

    // A body composed with no folder has to keep the shape it had before this product built any,
    // so an entity whose root never settled still registers the way it used to.
    [Fact]
    public void AnAddComposedWithNoFolderNamesNoPathAtAll()
    {
        Assert.False(
            ComposedBody.Of(V3BodyProjector.AddScene(SceneForeignId, AtTheRootAlone))
                .ContainsKey("path"));
        Assert.False(
            ComposedV2Body.Of(V2BodyProjector.RegisterSite(SiteNumber, AtTheRootAlone))
                .ContainsKey("path"));
    }

    // The schema this generation registers a studio under declares no path member at all, so a
    // folder is not expressible there and is never composed for one.
    [Fact]
    public void TheStudioSchemaThisGenerationRegistersUnderDeclaresNoPath()
        => Assert.Null(
            typeof(global::Whisparr3.Net.Model.StudioResource).GetProperty("Path"));
}
