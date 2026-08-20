using System.Reflection;

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

        // The second half of "what was examined", and the only observation in the repository that the
        // guard walks a class's BASE chain: this fixture declares no test method of its own, so a guard
        // that reads only declared methods never looks at it. See the fixture's own comment below for
        // what its absence would mean.
        Assert.Contains(typeof(InheritedOnlyTierFixture).FullName!, scan.Examined);

        Assert.True(
            scan.Examined.Count >= MinimumTestClasses,
            $"the guard examined only {scan.Examined.Count} test classes (floor {MinimumTestClasses}) — "
                + "discovery is broken, so an empty untagged list is not a pass");

        Assert.Empty(scan.Untagged);
    }

    // THE INHERITED-ONLY FIXTURE. This pair is the only class in the assembly whose test methods are
    // ALL inherited, so its presence in scan.Examined is the single observation that the guard walks
    // the class base chain rather than a class's own declared methods alone. Its absence means a class
    // in this shape is never examined at all: it could carry no Tier trait, be omitted from every
    // `--filter "Tier=Lx"` run, and still never reach the untagged list — the guard reporting "no
    // violations" about a class it never looked at.
    //
    // The concrete half carries a VALID Tier trait deliberately. What needs proving is that the class
    // is EXAMINED, which is the hole; making it a violation instead would redden the Assert.Empty above,
    // and relaxing that assertion to accommodate a fixture would delete the guarantee this file exists
    // for.

    /// <summary>Declares the pair's only test method, so the derived half inherits every one it has.</summary>
    public abstract class InheritedOnlyTierFixtureBase
    {
        // A real assertion, and it guards the fixture's own defining property: the moment the derived
        // half declares a test method of its own it stops being inherited-only and proves nothing about
        // the base-chain walk, while still passing. An empty body here would inspect nothing, which is
        // the shape this file refuses.
        [Fact]
        public void TheDerivedFixtureDeclaresNoTestMethodOfItsOwn() =>
            Assert.DoesNotContain(
                typeof(InheritedOnlyTierFixture).GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                method => method.GetCustomAttributes<FactAttribute>(inherit: true).Any());
    }

    /// <summary>
    /// The inherited-only class the guard must examine. Declares no test method of its own, and is
    /// Tier-tagged so that being examined is all it proves.
    /// </summary>
    [Trait("Tier", "L0")]
    public sealed class InheritedOnlyTierFixture : InheritedOnlyTierFixtureBase
    {
    }
}
