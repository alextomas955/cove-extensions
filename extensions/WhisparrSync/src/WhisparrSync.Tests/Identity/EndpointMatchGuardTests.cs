using WhisparrSync.Identity;

namespace WhisparrSync.Tests.Identity;

// Every expectation below was transcribed by hand from the host's own two private methods, never
// produced by calling the transcription this file checks. Some record behaviour that is surprising
// on its own terms; they are pinned as the host's answer, not as a preference.
public sealed class EndpointMatchGuardTests
{
    // The pair the host documents for one provider, in the host's own spelling.
    [Fact]
    public void TheHostsOwnWorkedPairIsOneSource()
    {
        Assert.True(EndpointMatchGuard.SameSource(
            "https://api.theporndb.net/", "https://theporndb.net/graphql"));
    }

    [Fact]
    public void ATrailingSeparatorAndSurroundingWhitespaceAreOneSource()
    {
        Assert.True(EndpointMatchGuard.SameSource(
            "https://stashdb.org/graphql/", "  https://stashdb.org/graphql  "));
    }

    [Fact]
    public void LetterCaseAloneIsOneSource()
    {
        Assert.True(EndpointMatchGuard.SameSource(
            "HTTPS://STASHDB.ORG/GRAPHQL", "https://stashdb.org/graphql"));
    }

    [Fact]
    public void DifferentHostsAndDifferentPathsOnOneDomainAreOneSource()
    {
        Assert.True(EndpointMatchGuard.SameSource(
            "https://api.stashdb.org/graphql", "https://stashdb.org/"));
    }

    [Fact]
    public void GenuinelyDifferentDomainsAreNotOneSource()
    {
        Assert.False(EndpointMatchGuard.SameSource(
            "https://stashdb.org/graphql", "https://theporndb.net/graphql"));
    }

    [Fact]
    public void AnAbsentEndpointIsNotTheSameSourceAsAPresentOne()
    {
        Assert.False(EndpointMatchGuard.SameSource(null, "https://stashdb.org/graphql"));
    }

    [Fact]
    public void AThreeLabelHostReducesToItsLastTwoLabels()
    {
        Assert.Equal("stashdb.org", EndpointMatchGuard.RegistrableDomain("https://api.stashdb.org/graphql"));
    }

    [Fact]
    public void ATwoLabelHostIsKeptWhole()
    {
        Assert.Equal("stashdb.org", EndpointMatchGuard.RegistrableDomain("https://stashdb.org"));
    }

    // The host's comment beside the rule says a leading www. is dropped. No step in the code drops
    // one: a three-label www host loses it to the two-label reduction, and a two-label one keeps
    // it. Both are pinned so a transcription written from the comment goes red.
    [Fact]
    public void AThreeLabelWwwHostLosesItsLeadingLabelAndATwoLabelOneDoesNot()
    {
        Assert.Equal("fansdb.cc", EndpointMatchGuard.RegistrableDomain("https://www.fansdb.cc"));
        Assert.Equal("www.cc", EndpointMatchGuard.RegistrableDomain("https://www.cc"));
    }

    // The host treats a multi-label public suffix as two labels, so two unrelated sites under one
    // such suffix read as one source.
    [Fact]
    public void AMultiLabelPublicSuffixIsTreatedAsTwoLabels()
    {
        Assert.Equal("co.uk", EndpointMatchGuard.RegistrableDomain("https://a.example.co.uk/graphql"));
        Assert.True(EndpointMatchGuard.SameSource(
            "https://a.example.co.uk/graphql", "https://b.other.co.uk/graphql"));
    }

    [Fact]
    public void AnInputThatIsNotAnAbsoluteUrlIsRetriedWithASchemePrepended()
    {
        Assert.Equal("stashdb.org", EndpointMatchGuard.RegistrableDomain("stashdb.org/graphql"));
    }

    [Fact]
    public void ABlankInputHasNoRegistrableDomain()
    {
        Assert.Equal("", EndpointMatchGuard.RegistrableDomain(null));
        Assert.Equal("", EndpointMatchGuard.RegistrableDomain(""));
        Assert.Equal("", EndpointMatchGuard.RegistrableDomain("   "));
    }

    // The domain arm requires the first endpoint to have a registrable domain, so a blank matches
    // nothing there, including another blank. The host still answers true for two blanks on the
    // normalisation arm above it, and that composite answer is what a caller sees.
    [Fact]
    public void TwoBlanksMatchOnTheNormalisationArmAndNotOnTheDomainArm()
    {
        Assert.Equal("", EndpointMatchGuard.RegistrableDomain(""));
        Assert.Equal("", EndpointMatchGuard.RegistrableDomain("   "));
        Assert.True(EndpointMatchGuard.SameSource("   ", ""));
    }
}
