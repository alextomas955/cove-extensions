using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

// The types this product composes and sends an outbound request on. Every reflection invariant over
// route literals and over fields folds this list rather than naming one type, so a seam that grows
// a type is covered by one edit here and no invariant quietly starts finding less.
internal static class OutboundSeamTypes
{
    public static IReadOnlyList<Type> All { get; } =
    [
        typeof(WhisparrTransport),
        typeof(WhisparrV3Instance),
        typeof(WhisparrV2Instance),
    ];

    // Every string constant the seam declares, whichever of its types holds it.
    public static IEnumerable<string> DeclaredLiterals()
        => All
            .SelectMany(type => type.GetFields(
                System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Static))
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string?)field.GetRawConstantValue())
            .OfType<string>();
}
