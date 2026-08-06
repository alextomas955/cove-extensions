using Renamer.Tests.TestSupport;

namespace Renamer.Tests;

/// <summary>
/// The extension's identity and metadata come from <c>extension.json</c>, never from a literal in
/// code. The host reads each value straight off the property, so an override here silently wins over
/// the shipped manifest — which is how a dead repository URL and a truncated description once reached
/// users, and how two copies of one id made a second instance of this extension unloadable.
/// </summary>
/// <remarks>
/// Deliberately constructs nothing. The two halves below — the manifest ships the right id, and the
/// code redeclares none of it — together pin that identity resolves from the manifest, while keeping
/// this class free of the Cove.Core runtime closure that every extension-constructing test needs. So
/// it is the one identity guard that still runs on the cove-absent CI leg, which is exactly the leg a
/// re-added override would otherwise sail through.
/// </remarks>
[Trait("Tier", "L0")]
public sealed class ExtensionIdentityTests
{
    // Hand-transcribed on purpose. Comparing against something derived from the manifest would make
    // the expectation agree with itself forever, including when the manifest is what broke.
    private const string ExpectedId = "com.alextomas955.renamer";
    private const string ExpectedName = "Renamer";

    [Fact]
    public void ShippedManifest_DeclaresTheIdentityTheHostWillRegisterUnder()
    {
        Assert.Equal(ExpectedId, RenamerFixture.Manifest.Id);
        Assert.Equal(ExpectedName, RenamerFixture.Manifest.Name);
    }

    // A regression here is not a wrong value but a REDECLARED one, which no value assertion can catch
    // while the copy still happens to agree with the manifest.
    [Theory]
    [InlineData("Id")]
    [InlineData("Name")]
    [InlineData("Version")]
    [InlineData("MinCoveVersion")]
    [InlineData("Description")]
    [InlineData("Author")]
    [InlineData("Url")]
    [InlineData("IconUrl")]
    [InlineData("Categories")]
    public void Metadata_IsNotRedeclaredInCode(string member)
    {
        var property = typeof(global::Renamer.Renamer).GetProperty(member);

        Assert.NotNull(property);
        Assert.NotEqual(typeof(global::Renamer.Renamer), property.DeclaringType);
    }
}
