using WhisparrSync.Options;

namespace WhisparrSync.Tests.Options;

/// <summary>
/// The required-stored-option predicate's full matrix. Every case pairs a positive (the option is unset, so
/// its key IS named) with a negative (the option is set, so its key is NEVER named), because a refusal that
/// names a setting the user already filled in sends them to the wrong control.
/// </summary>
[Trait("Tier", "L0")]
public sealed class ConfigCompletenessGuardTests
{
    private const string Address = "http://whisparr.local:6969";
    private const string Key = "STORED-KEY";

    private static WhisparrOptions Complete => new()
    {
        BaseUrl = Address,
        ApiKey = Key,
    };

    [Fact]
    public void CompleteOptions_NameNothing()
    {
        Assert.Empty(ConfigCompletenessGuard.MissingRequiredOptions(Complete));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankAddress_NamesOnlyTheAddress(string address)
    {
        var options = Complete with { BaseUrl = address };

        Assert.Equal(["baseUrl"], ConfigCompletenessGuard.MissingRequiredOptions(options));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankKey_NamesOnlyTheKey(string key)
    {
        var options = Complete with { ApiKey = key };

        Assert.Equal(["apiKey"], ConfigCompletenessGuard.MissingRequiredOptions(options));
    }

    [Fact]
    public void EverythingUnset_NamesBoth_InDeclaredSetupOrder()
    {
        var options = new WhisparrOptions { BaseUrl = "", ApiKey = "" };

        Assert.Equal(["baseUrl", "apiKey"], ConfigCompletenessGuard.MissingRequiredOptions(options));
    }

    // ---- the negatives: a SUPPLIED option is never named, one case per key ----

    [Fact]
    public void SuppliedAddress_IsNeverNamed()
    {
        var options = new WhisparrOptions { BaseUrl = Address, ApiKey = "" };

        Assert.DoesNotContain("baseUrl", ConfigCompletenessGuard.MissingRequiredOptions(options));
    }

    [Fact]
    public void SuppliedKey_IsNeverNamed()
    {
        var options = new WhisparrOptions { BaseUrl = "", ApiKey = Key };

        Assert.DoesNotContain("apiKey", ConfigCompletenessGuard.MissingRequiredOptions(options));
    }

    // The two metadata-endpoint settings are advanced and not surfaced in the settings UI, so naming one would
    // send the user to a control that does not exist. They are required-looking but deliberately unguarded.
    [Fact]
    public void AdvancedUnsurfacedSettings_AreNeverNamed()
    {
        var options = Complete with { StashDbEndpoint = "", TpdbEndpoint = "" };

        Assert.Empty(ConfigCompletenessGuard.MissingRequiredOptions(options));
    }
}
