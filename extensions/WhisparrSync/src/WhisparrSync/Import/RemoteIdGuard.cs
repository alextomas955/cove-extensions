using System.Globalization;

namespace WhisparrSync.Import;

/// <summary>Whether an identifier an instance rendered names a scene at all.</summary>
/// <remarks>
/// Pure. Each lineage carries its own metadata source's own identifier, and each has a rendering
/// meaning the entity was never matched to a scene: one leaves its member absent or blank, the other
/// carries a number whose zero is its unset value. An unset rendering taken as an identifier makes
/// every unmatched scene the same scene, and an arrival carrying one then re-points onto whichever
/// item was stamped with it first.
/// <para>
/// Both channels read through here, so neither can accept an identifier the other would refuse.
/// </para>
/// </remarks>
internal static class RemoteIdGuard
{
    /// <summary><paramref name="rendered"/> when it names a scene, or null when it names none.</summary>
    internal static string? Identifying(string? rendered)
        => string.IsNullOrWhiteSpace(rendered) || IsUnset(rendered) ? null : rendered;

    private static bool IsUnset(string rendered)
        => long.TryParse(rendered, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number == 0;
}
