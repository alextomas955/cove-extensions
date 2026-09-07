namespace WhisparrSync.Tests;

/// <summary>
/// Enforcement point for the Tier-trait invariant in this assembly: a test case with no Tier trait is
/// invisible to a <c>dotnet test --filter "Tier=Lx"</c> selection, and one carrying two is selected by
/// both — so the tiers must partition the suite, not merely cover it.
/// </summary>
[Trait("Tier", "L0")]
public sealed class TierTraitCoverageTests
{
    [Fact]
    public void EveryTestCaseResolvesToExactlyOneTier()
    {
        IReadOnlyList<string> offenders =
            TierTraitGuard.TierTraitOffenders(typeof(TierTraitCoverageTests).Assembly);

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} test case(s) do not resolve to exactly one [Trait(\"Tier\", \"L0\"..\"L3\")]:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, offenders));
    }
}
