using System.Reflection;

namespace Cove.Extensions.Shared.Testing;

/// <summary>
/// The result of one <see cref="TierTraitGuard.Scan"/> pass.
/// </summary>
/// <param name="Examined">
/// Full names of every discoverable xUnit test class the pass inspected, sorted ordinally. This is
/// what makes <paramref name="Untagged"/> mean anything — see the remarks on
/// <see cref="TierTraitGuard.Scan"/>.
/// </param>
/// <param name="Untagged">
/// Full names of the inspected classes carrying no class-level Tier trait in <c>L0..L3</c>, sorted
/// ordinally. Empty is the passing state.
/// </param>
public sealed record TierTraitScan(IReadOnlyList<string> Examined, IReadOnlyList<string> Untagged);

/// <summary>
/// Reflection guard that enumerates the xUnit test classes in a test assembly and reports any that
/// lack a class-level <c>[Trait("Tier", "L0"|"L1"|"L2"|"L3")]</c>.
/// </summary>
/// <remarks>
/// A <c>--filter-trait "Tier=Lx"</c> selection silently omits a class with no Tier trait; this guard
/// is the enforcement point for that invariant. Traits are read by attribute type NAME rather than by
/// type identity, so the guard still loads across an xUnit major version that moves or renames the
/// attribute's assembly.
/// </remarks>
public static class TierTraitGuard
{
    private static readonly string[] ValidTiers = ["L0", "L1", "L2", "L3"];

    /// <summary>
    /// Scans <paramref name="assembly"/> once for discoverable xUnit test classes and reports both
    /// what was examined and what is untagged.
    /// </summary>
    /// <remarks>
    /// Both lists come from ONE enumeration so they cannot disagree: an empty <c>Untagged</c> is only
    /// meaningful next to the <c>Examined</c> set that produced it, and a caller that asserts the
    /// first without the second passes just as happily on zero input.
    /// </remarks>
    public static TierTraitScan Scan(Assembly assembly)
    {
        List<Type> testClasses = [.. LoadableTypes(assembly)
            .Where(t => t.IsClass && !t.IsAbstract && IsTestClass(t))];

        return new TierTraitScan(
            [.. testClasses.Select(FullNameOf).OrderBy(name => name, StringComparer.Ordinal)],
            [.. testClasses.Where(t => !HasValidTierTrait(t))
                .Select(FullNameOf)
                .OrderBy(name => name, StringComparer.Ordinal)]);
    }

    private static string FullNameOf(Type type) => type.FullName ?? type.Name;

    private static Type[] LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // A partially-loadable assembly still yields its loadable types; the null slots are the
            // types that failed to load and cannot be a tagged-or-not test class here.
            return ex.Types.Where(t => t is not null).ToArray()!;
        }
    }

    private static bool IsTestClass(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.CustomAttributes.Any(a => IsFactLike(a.AttributeType)));

    // Two different base chains are walked in this region.
    //
    // IsTestClass above walks the CLASS chain, and BindingFlags governs it: GetMethods returns a
    // class's INHERITED public instance methods unless DeclaredOnly is passed. With that flag, a class
    // whose only fact-like methods are declared on a base is hidden here while xUnit still discovers
    // and runs it — so it could carry no Tier trait, be omitted from every --filter-trait "Tier=Lx"
    // selection, and never appear in Untagged. Omitting the flag does not widen the match: the public
    // instance methods a class inherits for free are System.Object's, and none is fact-like. Duplicates
    // are possible (a method hidden with `new` is reported once per declaring type) and immaterial,
    // since Any folds the set to one bool.
    //
    // IsFactLike below walks the ATTRIBUTE chain, which no BindingFlags reaches: [Theory] and anything
    // else deriving from Xunit.FactAttribute is caught by name, with no xUnit type reference. Matching
    // by name rather than by typeof is what keeps the guard from passing vacuously against an assembly
    // built on a different xUnit identity.
    private static bool IsFactLike(Type? attributeType)
    {
        for (var t = attributeType; t is not null; t = t.BaseType)
        {
            if (t.FullName == "Xunit.FactAttribute")
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasValidTierTrait(Type type) =>
        type.GetCustomAttributesData().Any(a =>
            a.AttributeType.FullName == "Xunit.TraitAttribute"
            && a.ConstructorArguments.Count == 2
            && a.ConstructorArguments[0].Value as string == "Tier"
            && ValidTiers.Contains(a.ConstructorArguments[1].Value as string));
}
