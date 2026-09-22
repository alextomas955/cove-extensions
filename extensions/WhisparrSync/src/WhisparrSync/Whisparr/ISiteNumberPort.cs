namespace WhisparrSync.Whisparr;

/// <summary>The number a site is named by, or why none was established.</summary>
/// <remarks>
/// Whisparr v2 names a site by a number of its own rather than by the identifier Cove holds, and
/// every v2 route addressing a site takes that number.
/// <para>
/// A read that never arrived and an answer naming no site are held apart, so a caller cannot report
/// one as the other.
/// </para>
/// </remarks>
public sealed record WhisparrSiteNumber
{
    private WhisparrSiteNumber(int? number, bool wasReached)
    {
        Number = number;
        WasReached = wasReached;
    }

    /// <summary>The number the site is named by, or null.</summary>
    public int? Number { get; }

    /// <summary>The question was answered, whatever it was answered with.</summary>
    public bool WasReached { get; }

    /// <summary>Nothing was reached, so nothing is known about the site.</summary>
    public static WhisparrSiteNumber NotReached { get; } = new(null, false);

    /// <summary>There is no number for the identifier the library holds.</summary>
    public static WhisparrSiteNumber NamesNone { get; } = new(null, true);

    /// <summary>The site is named by <paramref name="number"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="number"/> is not positive, which names no site on either generation.
    /// </exception>
    public static WhisparrSiteNumber Numbered(int number)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        return new WhisparrSiteNumber(number, true);
    }
}

/// <summary>Turns the identifier the library holds for a studio into the number a site is named by.</summary>
/// <remarks>
/// The instance answers this, not the metadata source Cove is configured with. The instance's own
/// lookup resolves the stored identifier, so a source request per studio is not paid and the run
/// does not fail where the source is unreachable.
/// </remarks>
public interface ISiteNumberPort
{
    /// <summary>What the site <paramref name="storedSiteId"/> names is numbered.</summary>
    /// <remarks>
    /// One resolution per site, and nothing is held between calls. How many run at once is the
    /// caller's to bound.
    /// </remarks>
    Task<WhisparrSiteNumber> ResolveSiteNumberAsync(
        WhisparrBinding binding, string storedSiteId, CancellationToken ct);
}
