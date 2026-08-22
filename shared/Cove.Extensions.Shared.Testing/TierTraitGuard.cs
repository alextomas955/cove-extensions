using System.Reflection;

namespace Cove.Extensions.Shared.Testing;

/// <summary>
/// Reflection guard that enumerates the xUnit test classes in a test assembly and reports any that
/// lack a class-level <c>[Trait("Tier", "L0"|"L1"|"L2"|"L3")]</c>.
/// </summary>
/// <remarks>
/// A <c>--filter-trait "Tier=Lx"</c> selection silently omits a class with no Tier trait; this guard
/// is the enforcement point for that invariant.
/// </remarks>
public static class TierTraitGuard
{
    private static readonly string[] ValidTiers = ["L0", "L1", "L2", "L3"];

    /// <summary>
    /// Returns the full names of every non-abstract xUnit test class in <paramref name="assembly"/>
    /// that is missing a class-level Tier trait with a value in <c>L0..L3</c>, sorted ordinally.
    /// </summary>
    public static IReadOnlyList<string> UntaggedTestClasses(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // A partially-loadable assembly still yields its loadable types; the null slots are the
            // types that failed to load and cannot be a tagged-or-not test class here.
            types = ex.Types.Where(t => t is not null).ToArray()!;
        }

        return types
            .Where(t => t.IsClass && !t.IsAbstract && IsTestClass(t) && !HasValidTierTrait(t))
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsTestClass(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Any(m => m.CustomAttributes.Any(a => IsFactLike(a.AttributeType)));

    // [Theory] derives from Xunit.FactAttribute, so walking the base chain catches every discoverable
    // test method.
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
