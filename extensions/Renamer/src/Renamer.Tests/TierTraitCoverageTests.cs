namespace Renamer.Tests;

/// <summary>
/// Enforcement point for the Tier-trait invariant in this assembly: a class with no class-level
/// Tier trait is invisible to a <c>--filter "Tier=Lx"</c> selection.
/// </summary>
[Trait("Tier", "L0")]
public sealed class TierTraitCoverageTests
{
    // Well below the ~90 test classes here, and only ever crossed downward by deleting most of the
    // suite — which is itself worth a red build. A floor cannot go stale upward.
    private const int MinimumTestClasses = 50;

    [Fact]
    public void AllTestClassesCarryATierTrait()
    {
        TierTraitScan scan = TierTraitGuard.Scan(GetType().Assembly);

        // An empty untagged list proves nothing on its own: the guard matches xUnit's attributes by
        // NAME, so an xUnit release that moves or renames FactAttribute would make discovery match
        // zero classes and report "no violations" while inspecting nothing. These two assertions are
        // what make the third one evidence. The first needs no maintenance — it names the class it
        // lives in, which is itself a discoverable test class, so it tracks any rename automatically.
        Assert.Contains(typeof(TierTraitCoverageTests).FullName!, scan.Examined);
        Assert.True(
            scan.Examined.Count >= MinimumTestClasses,
            $"the guard examined only {scan.Examined.Count} test classes (floor {MinimumTestClasses}) — "
                + "discovery is broken, so an empty untagged list is not a pass");

        Assert.Empty(scan.Untagged);
    }
}
