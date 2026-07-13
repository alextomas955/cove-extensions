using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests;

/// <summary>
/// The fail-closed version-gate proof (VER-04): the major is parsed from the dotted version string, a v3
/// instance selects the <see cref="V3Adapter"/>, and every other version — including an unparseable one —
/// is refused with a <c>null</c> selection rather than a silent wrong-adapter call.
/// </summary>
public sealed class AdapterSelectorTests
{
    private static WhisparrClient AnyClient()
        => new(new HttpClient(FakeHttpMessageHandler.Json("{}")));

    private static SystemStatus StatusWith(string? version)
        => new(version, "Whisparr", "My Whisparr", "eros-develop");

    [Theory]
    [InlineData("3.3.4.808", 3)]
    [InlineData("3", 3)]
    [InlineData("2.0.2.1", 2)]
    [InlineData("10.1.0.0", 10)]
    public void ParseMajor_extracts_the_leading_dotted_segment(string version, int expected)
    {
        Assert.Equal(expected, AdapterSelector.ParseMajor(version));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("eros")]
    [InlineData("v3.1")]
    [InlineData(".3.4")]
    public void ParseMajor_returns_minus_one_for_null_or_garbage(string? version)
    {
        Assert.Equal(-1, AdapterSelector.ParseMajor(version));
    }

    [Fact]
    public void Select_returns_v3_adapter_when_major_is_3()
    {
        var adapter = AdapterSelector.Select(StatusWith("3.3.4.808"), AnyClient());

        Assert.NotNull(adapter);
        Assert.IsType<V3Adapter>(adapter);
    }

    [Fact]
    public void Select_refuses_a_v2_instance()
    {
        Assert.Null(AdapterSelector.Select(StatusWith("2.0.2.1"), AnyClient()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("eros")]
    public void Select_refuses_an_unparseable_version(string? version)
    {
        Assert.Null(AdapterSelector.Select(StatusWith(version), AnyClient()));
    }
}
