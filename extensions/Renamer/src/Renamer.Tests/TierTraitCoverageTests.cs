namespace Renamer.Tests;

/// <summary>
/// Enforcement point for the Tier-trait invariant in this assembly: a class with no class-level
/// Tier trait is invisible to a <c>--filter "Tier=Lx"</c> selection.
/// </summary>
[Trait("Tier", "L0")]
public sealed class TierTraitCoverageTests
{
    // This project compiles as two different assemblies: with the `../cove` sibling present it carries
    // 120 test classes, and on the bare leg CI runs the cove-referencing sources are Compile-Removed
    // (see Renamer.Tests.csproj), leaving 70 — both measured 2026-08-14 by reading this assertion's own
    // message. A single floor has to clear the SMALLER leg, so it is derived from 70, not from 120.
    //
    // What the floor is FOR: proving discovery is not broken. The guard matches xUnit's attributes by
    // NAME, so a collapse examines ~0 classes and the untagged list below is then empty for the wrong
    // reason. It is NOT a coverage floor, and reading it as one is the mistake to avoid: an accidental
    // shrink is caught by the per-batch measured bare-leg class count — a real number compared against
    // a recorded one — whereas this threshold sits well below the truth by construction.
    //
    // 45 = 70 measured, minus the ~17 bare-leg classes Phase 35 removes deliberately, minus 8 more as
    // headroom, so it still clears even if that estimate is 50% low. Re-derived here, deliberately and
    // ahead of the batch that crosses the old value, because two batches in one wave each remove
    // classes without being able to observe the other's result. Phase 35-07 tightens it to the measured
    // final count. Never raise or lower it reactively when it goes red: a red is either a mass deletion
    // or broken discovery, and both are worth the build.
    private const int MinimumTestClasses = 45;

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
