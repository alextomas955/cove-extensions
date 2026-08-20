using System.Reflection;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Contracts;

/// <summary>
/// The pin that keeps the shipped manifest's stated permission reach equal to the backend's real one.
/// </summary>
/// <remarks>
/// The manifest has no structured per-kind permission field, so the claim an operator reads before
/// granting the extension access lives in the description prose. Prose has no compiler, which makes an
/// understatement here silent: the endpoints go on accepting a kind the manifest never mentions, and
/// permission review is the surface that pays for it.
/// <para>
/// The direction of this pin is what earns it. The expectation is owned by the code the manifest
/// DESCRIBES — the backend's own renamable kinds, and the permission pair the backend itself returns
/// for each — so a kind added to the backend fails this test until the manifest names its permissions.
/// A test holding its own list of kind names would instead agree with itself forever and go stale in
/// exactly the way the manifest already did.
/// </para>
/// <para>
/// What the pin deliberately does NOT assert is the reach of the registered bulk actions or of the
/// auto-rename hook. Both cover fewer kinds than the endpoints do, on purpose, so widening this pin to
/// them would demand a manifest sentence that is false.
/// </para>
/// </remarks>
[Trait("Tier", "L0")]
public sealed class ManifestPermissionPinTests
{
    [Fact]
    public void EveryRenamableKindsPermissionPairIsNamedInTheShippedManifestDescription()
    {
        // The shipped bytes, through the one seam that reads them, rather than a second path literal.
        var description = RenamerFixture.Manifest.Description;
        Assert.False(
            string.IsNullOrWhiteSpace(description),
            "extension.json declares no description. The per-kind permission claim lives there and "
                + "nowhere else, so an empty one states nothing to an operator granting access.");

        var kinds = RenamableKinds();
        Assert.NotEmpty(kinds);

        foreach (var kind in kinds)
        {
            var (read, write) = global::Renamer.Renamer.PermissionsFor(kind);

            Assert.Contains(read, description, StringComparison.Ordinal);
            Assert.Contains(write, description, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Reads the backend's renamable-kind set out of the field that defines it. Reflection because the
    /// field is private and this pin is not a reason to widen its visibility; the null check is what
    /// keeps a rename of the field a loud failure instead of a pin that inspects nothing.
    /// </summary>
    private static RenamerFileKind[] RenamableKinds()
    {
        var field = typeof(global::Renamer.Renamer).GetField(
            "RenamableKinds",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);

        return Assert.IsType<RenamerFileKind[]>(field.GetValue(null));
    }
}
