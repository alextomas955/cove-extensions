using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

// The crossing from a typed setting to a thing requests are sent on. Every refusal here is a
// refusal the address reader already makes, asserted against the binding so the two cannot drift.
public sealed class WhisparrBindingTests
{
    private const string Key = "7c7c7c7c7c7c7c7c7c7c7c7c7c7c7c7c";

    [Fact]
    public void AnAbsoluteHttpAddressAKeyAndAGenerationAreCarriedTogether()
    {
        var binding = new WhisparrBinding(
            WhisparrGeneration.V3, new Uri("http://whisparr-v3:6969/"), Key);

        Assert.Equal(WhisparrGeneration.V3, binding.Generation);
        Assert.Equal(new Uri("http://whisparr-v3:6969/"), binding.BaseAddress);
        Assert.Equal(Key, binding.ApiKey);
    }

    [Fact]
    public void AnHttpsAddressUnderAPathIsCarriedWithThatPath()
    {
        var binding = new WhisparrBinding(
            WhisparrGeneration.V2, new Uri("https://host.example/whisparr"), Key);

        Assert.Equal(new Uri("https://host.example/whisparr"), binding.BaseAddress);
    }

    [Fact]
    public void ARelativeAddressIsRefused()
        => Assert.Throws<ArgumentException>(
            () => new WhisparrBinding(
                WhisparrGeneration.V3, new Uri("/api/v3", UriKind.Relative), Key));

    [Theory]
    [InlineData("ftp://host.example/")]
    [InlineData("file:///c:/whisparr")]
    public void AnAddressOnNeitherHttpNorHttpsIsRefused(string address)
        => Assert.Throws<ArgumentException>(
            () => new WhisparrBinding(WhisparrGeneration.V3, new Uri(address), Key));

    [Fact]
    public void NoAddressIsRefused()
        => Assert.Throws<ArgumentNullException>(
            () => new WhisparrBinding(WhisparrGeneration.V3, null!, Key));

    // Matching what the address reader does: the authority is rebuilt, so credentials a user
    // embedded cannot travel on the binding.
    [Fact]
    public void AnAddressCarryingUserInfoDoesNotKeepIt()
    {
        var binding = new WhisparrBinding(
            WhisparrGeneration.V3, new Uri("http://someone:secret@host.example:6969/"), Key);

        Assert.Equal(new Uri("http://host.example:6969/"), binding.BaseAddress);
        Assert.Equal("", binding.BaseAddress.UserInfo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AKeyThatIsBlankIsRefused(string? apiKey)
        => Assert.Throws<ArgumentException>(
            () => new WhisparrBinding(
                WhisparrGeneration.V3, new Uri("http://host.example:6969/"), apiKey!));

    [Fact]
    public void TheRenderedBindingDoesNotCarryTheKey()
    {
        var rendered = new WhisparrBinding(
            WhisparrGeneration.V3, new Uri("http://host.example:6969/"), Key).ToString();

        Assert.DoesNotContain(Key, rendered, StringComparison.Ordinal);
        Assert.Contains("host.example", rendered, StringComparison.Ordinal);
    }
}
