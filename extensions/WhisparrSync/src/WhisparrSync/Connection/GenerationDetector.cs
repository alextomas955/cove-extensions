using System.Globalization;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Connection;

/// <summary>What a status document says about which generation answered.</summary>
/// <remarks>
/// <c>Corroborated</c> is whether the branch and the count fields agree with the version major, or
/// null when the major names no generation this product manages. False is reported rather than
/// resolved.
/// </remarks>
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
/// The decision is the <c>version</c> major and nothing else: the API path is <c>/api/v3</c> on both
/// generations and <c>appName</c> reads the same on both, so neither discriminates.
/// <para>
/// <c>branch</c> and the count fields are a separate corroboration reading rather than a second vote,
/// so a disagreement with the version surfaces instead of being averaged away. A major this product
/// does not manage yields no generation.
/// </para>
/// </remarks>
public static class GenerationDetector
{
    internal const string ErosBranch = "eros";

    internal const string V2Branch = "v2";

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
