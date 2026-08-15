namespace Renamer.Tests;

/// <summary>
/// Enforcement point for the Tier-trait invariant in this assembly: a class with no class-level
/// Tier trait is invisible to a <c>--filter "Tier=Lx"</c> selection.
/// </summary>
[Trait("Tier", "L0")]
public sealed class TierTraitCoverageTests
{
    // This project compiles as two different assemblies: with the `../cove` sibling present it carries
    // 89 test classes, and on the bare leg CI runs the cove-referencing sources are Compile-Removed
    // (see Renamer.Tests.csproj), leaving 54 — both measured 2026-08-15 by raising this constant until
    // the assertion below failed and reading the count out of its own message. A single floor has to
    // clear the SMALLER leg, so it is derived from 54, not from 89.
    //
    // What the floor is FOR: proving discovery is not broken. The guard matches xUnit's attributes by
    // NAME, so a release that renamed or moved FactAttribute would make discovery match zero classes,
    // leaving the untagged list below empty for the wrong reason and reporting "no violations" while
    // inspecting nothing. It is NOT a coverage floor, and reading it as one is the mistake to avoid: an
    // accidental shrink is caught by comparing a measured bare-leg class count against a recorded one,
    // whereas this threshold sits below the truth by construction.
    //
    // 50 = 54 measured, minus 4 so ordinary attrition does not turn a green build red. Never raise or
    // lower it reactively when it goes red: a red is either a mass deletion or broken discovery, and
    // both are worth the build.
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
