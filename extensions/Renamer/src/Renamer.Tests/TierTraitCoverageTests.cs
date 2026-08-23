namespace Renamer.Tests;

/// <summary>
/// Enforcement point for the Tier-trait invariant in this assembly: a class with no class-level
/// Tier trait is invisible to a <c>--filter-trait "Tier=Lx"</c> selection.
/// </summary>
[Trait("Tier", "L0")]
public sealed class TierTraitCoverageTests
{
    [Fact]
    public void AllTestClassesCarryATierTrait()
    {
        IReadOnlyList<string> missing = TierTraitGuard.UntaggedTestClasses(GetType().Assembly);

        Assert.Empty(missing);
    }
}
