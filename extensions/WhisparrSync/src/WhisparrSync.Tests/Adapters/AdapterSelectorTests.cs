using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Adapters;

/// <summary>
/// The version-gate proof: the major is parsed from the dotted version string, a v3
/// instance selects the <see cref="V3Adapter"/>, a v2 instance selects the <see cref="V2Adapter"/>, and
/// every other version — including an unparseable one — is still refused with a <c>null</c> selection
/// rather than a silent wrong-adapter call (fail-closed preserved for major != 2, 3).
/// </summary>
[Trait("Tier", "L0")]
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
    public void Select_returns_v2_adapter_when_major_is_2()
    {
        var adapter = AdapterSelector.Select(StatusWith("2.0.2.1"), AnyClient());

        Assert.NotNull(adapter);
        Assert.IsType<V2Adapter>(adapter);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("eros")]
    [InlineData("1.0.0.0")]
    [InlineData("10.1.0.0")]
    public void Select_refuses_an_unparseable_or_unsupported_version(string? version)
    {
        // Fail-closed: only major 2 and 3 are supported — everything else (garbage OR a
        // parseable-but-unmanaged major) is refused with null, never a wrong-adapter call.
        Assert.Null(AdapterSelector.Select(StatusWith(version), AnyClient()));
    }

    [Fact]
    public void SelectForVersion_returns_v3_adapter_for_v3()
    {
        Assert.IsType<V3Adapter>(AdapterSelector.SelectForVersion("v3", AnyClient()));
    }

    [Theory]
    [InlineData("v2")]
    [InlineData("V2")]
    public void SelectForVersion_returns_v2_adapter_for_v2_case_insensitive(string selected)
    {
        Assert.IsType<V2Adapter>(AdapterSelector.SelectForVersion(selected, AnyClient()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1")]
    [InlineData("v10")]
    public void SelectForVersion_refuses_an_unsupported_selection(string? selected)
    {
        Assert.Null(AdapterSelector.SelectForVersion(selected, AnyClient()));
    }

    [Fact]
    public void SelectForVersion_with_the_capability_returns_the_catalogue_carrying_v3_adapter()
    {
        var adapter = AdapterSelector.SelectForVersion(
            "v3", AnyClient(), new WhisparrCapabilities(NarrowEntityCatalogue: true));

        Assert.IsType<V3EntityCatalogueAdapter>(adapter);
    }

    [Fact]
    public void SelectForVersion_without_the_capability_returns_the_plain_v3_adapter()
    {
        var adapter = AdapterSelector.SelectForVersion("v3", AnyClient(), WhisparrCapabilities.None);

        Assert.IsType<V3Adapter>(adapter);
    }

    [Theory]
    [InlineData("v2")]
    [InlineData("v1")]
    public void SelectForVersion_ignores_the_capability_on_every_non_v3_selection(string selected)
    {
        // The capability names a v3 route pair; a v2 or unmanageable selection resolves exactly as it does
        // without one, so a stale capability answer can never change which generation is selected.
        var withCapability = AdapterSelector.SelectForVersion(
            selected, AnyClient(), new WhisparrCapabilities(NarrowEntityCatalogue: true));

        Assert.Equal(AdapterSelector.SelectForVersion(selected, AnyClient())?.GetType(), withCapability?.GetType());
    }
}
