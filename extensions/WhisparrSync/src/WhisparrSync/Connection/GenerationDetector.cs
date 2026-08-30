using System.Globalization;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Connection;

/// <summary>What a status document says about which generation answered.</summary>
/// <param name="Generation">
/// The generation the version major decides, or null when this product manages neither.
/// </param>
/// <param name="Version">The instance's own version string, verbatim.</param>
/// <param name="Branch">The branch the instance names.</param>
/// <param name="CountFieldsPresent">Whether all four count fields are present.</param>
/// <param name="Corroborated">
/// Whether the branch and the count fields agree with the decision, or null when there is no
/// decision for them to agree with. False is a BUILD GAP finding: a document whose two corroborating
/// readings contradict its own version major is reported rather than resolved.
/// </param>
public sealed record GenerationReading(
    WhisparrGeneration? Generation,
    string? Version,
    string? Branch,
    bool CountFieldsPresent,
    bool? Corroborated);

/// <summary>
/// Decides which Whisparr generation a status document came from.
/// </summary>
/// <remarks>
/// Pure. The decision is the <c>version</c> MAJOR and nothing else: the API path is <c>/api/v3</c> on
/// both generations, and <c>appName</c> reads the same on both, so neither discriminates.
/// <para>
/// <c>branch</c> and the count fields are returned as a separate corroboration reading rather than as
/// a second vote, so a disagreement between them and the version surfaces instead of being averaged
/// away. The detector never guesses an adapter for a major it does not manage.
/// </para>
/// </remarks>
public static class GenerationDetector
{
    /// <summary>The branch v3 names.</summary>
    internal const string ErosBranch = "eros";

    /// <summary>The branch v2 names.</summary>
    internal const string V2Branch = "v2";

    /// <summary>What <paramref name="document"/> says about its generation.</summary>
    public static GenerationReading Detect(WhisparrStatusDocument? document)
    {
        if (document is null)
        {
            return new GenerationReading(null, null, null, false, null);
        }

        var generation = GenerationOf(document.Version);
        var countFieldsPresent = document.AllCountFieldsPresent;

        bool? corroborated = generation switch
        {
            WhisparrGeneration.V3 => IsBranch(document.Branch, ErosBranch) && countFieldsPresent,
            WhisparrGeneration.V2 => IsBranch(document.Branch, V2Branch) && document.NoCountFieldsPresent,
            _ => null,
        };

        return new GenerationReading(
            generation,
            document.Version,
            document.Branch,
            countFieldsPresent,
            corroborated);
    }

    /// <summary>The generation <paramref name="version"/>'s major names, or null for any other.</summary>
    internal static WhisparrGeneration? GenerationOf(string? version)
        => MajorOf(version) switch
        {
            3 => WhisparrGeneration.V3,
            2 => WhisparrGeneration.V2,
            _ => null,
        };

    private static int? MajorOf(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var major = version.Split('.', 2)[0];
        return int.TryParse(major, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static bool IsBranch(string? branch, string expected)
        => branch is not null && branch.Equals(expected, StringComparison.OrdinalIgnoreCase);
}
