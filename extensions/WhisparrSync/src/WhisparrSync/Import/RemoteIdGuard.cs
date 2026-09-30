using System.Globalization;

namespace WhisparrSync.Import;

// Pure. An identifier is unset when it is absent or blank, or when it is the number zero. An unset
// rendering taken as an identifier makes every unmatched scene the same scene, and an arrival
// carrying one then re-points onto whichever item was stamped with it first.
// Both channels read through here, so neither can accept an identifier the other would refuse.
internal static class RemoteIdGuard
{
    internal static string? Identifying(string? rendered)
        => string.IsNullOrWhiteSpace(rendered) || IsUnset(rendered) ? null : rendered;

    private static bool IsUnset(string rendered)
        => long.TryParse(rendered, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number == 0;
}
