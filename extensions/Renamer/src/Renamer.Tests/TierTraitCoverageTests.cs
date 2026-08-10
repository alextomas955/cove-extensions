namespace Renamer.Tests;

/// <summary>
/// Enforcement point for the Tier-trait invariant in this assembly: a class with no class-level
/// Tier trait is invisible to a <c>--filter "Tier=Lx"</c> selection.
/// </summary>
[Trait("Tier", "L0")]
public sealed class TierTraitCoverageTests
{
    // This project compiles as two different assemblies. With the `../cove` sibling present it carries
    // ~92 test classes; on the bare leg CI runs, the cove-referencing sources are Compile-Removed
    // (see Renamer.Tests.csproj) and 44 remain. A single floor therefore has to clear the SMALLER leg,
    // measured at 44 — so this is deliberately not "~90 minus a bit". It only ever trips on a mass
    // deletion, which is itself worth a red build, and it cannot go stale upward.
    private const int MinimumTestClasses = 40;

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
