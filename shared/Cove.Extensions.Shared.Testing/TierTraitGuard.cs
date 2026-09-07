using System.Reflection;

namespace Cove.Extensions.Shared.Testing;

/// <summary>
/// Reflection guard that enumerates the xUnit test methods in a test assembly and reports any whose
/// resolved Tier trait is not exactly one of <c>L0</c>/<c>L1</c>/<c>L2</c>/<c>L3</c>.
/// </summary>
/// <remarks>
/// <para>
/// A <c>--filter "Tier=Lx"</c> selection silently omits a test with no Tier trait, and DOUBLE-COUNTS one
/// carrying two: xUnit unions the class-level and method-level traits, so a method-level
/// <c>[Trait("Tier","L3")]</c> on a class-level <c>L0</c> class resolves to both and is selected by both
/// filters. Reporting per resolved test case — class ∪ method, exactly one value — is what makes the
/// tiers partition the suite rather than merely cover it. Traits are read by attribute type name to keep
/// an xUnit package reference out of this shared assembly.
/// </para>
/// </remarks>
public static class TierTraitGuard
{
    private static readonly string[] ValidTiers = ["L0", "L1", "L2", "L3"];

    private const BindingFlags TestMethods =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    /// <summary>
    /// Returns one entry per xUnit test method in <paramref name="assembly"/> whose resolved Tier trait
    /// set is not exactly one valid tier, sorted ordinally. An entry names the offending member and what
    /// was resolved, so a failure says which tier values collided rather than only that one is missing.
    /// </summary>
    public static IReadOnlyList<string> TierTraitOffenders(Assembly assembly)
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

        var offenders = new List<string>();
        foreach (var type in types.Where(t => t.IsClass && !t.IsAbstract))
        {
            var classTiers = TierValues(type.GetCustomAttributesData());
            foreach (var method in type.GetMethods(TestMethods))
            {
                var attributes = method.GetCustomAttributesData();
                if (!attributes.Any(a => IsFactLike(a.AttributeType)))
                {
                    continue;
                }

                // xUnit UNIONS class- and method-level traits, so the resolved set is what a --filter
                // actually selects on. Distinct(): the same value stated in both places selects once and
                // is redundant, not a partition break.
                var resolved = classTiers.Concat(TierValues(attributes)).Distinct(StringComparer.Ordinal).ToList();
                if (resolved.Count == 1 && ValidTiers.Contains(resolved[0], StringComparer.Ordinal))
                {
                    continue;
                }

                string found = resolved.Count == 0
                    ? "no Tier trait"
                    : $"Tier=[{string.Join(", ", resolved.OrderBy(v => v, StringComparer.Ordinal))}]";
                offenders.Add($"{type.FullName ?? type.Name}.{method.Name}: {found}");
            }
        }

        return offenders.OrderBy(name => name, StringComparer.Ordinal).ToList();
    }

    // Every Tier value a Trait attribute on this member declares, valid or not — an invalid value must
    // reach the caller as a reported offender rather than being filtered into "no Tier trait".
    private static List<string> TierValues(IEnumerable<CustomAttributeData> attributes) =>
        attributes
            .Where(a =>
                a.AttributeType.FullName == "Xunit.TraitAttribute"
                && a.ConstructorArguments.Count == 2
                && a.ConstructorArguments[0].Value as string == "Tier")
            .Select(a => a.ConstructorArguments[1].Value as string ?? string.Empty)
            .ToList();

    // [Theory], [SkippableFact] and [SkippableTheory] all derive from Xunit.FactAttribute, so walking
    // the base chain by name catches every discoverable test method without an xUnit type reference.
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
}
