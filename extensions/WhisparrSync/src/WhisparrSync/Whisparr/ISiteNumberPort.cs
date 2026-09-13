namespace WhisparrSync.Whisparr;

/// <summary>The number a site is named by, or why none was established.</summary>
/// <remarks>
/// Whisparr v2 names a site by a number of its own rather than by the identifier Cove holds, and
/// every route that addresses a site there is addressed by that number.
/// <para>
/// A read that never arrived names no site and states no absence, and the two are held apart so a
/// caller cannot report one as the other. One site nothing could be established for is a site this
/// run leaves alone; a read that arrived at nothing says nothing about the site at all.
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
public interface ISiteNumberPort
{
    /// <summary>What the site <paramref name="storedSiteId"/> names is numbered.</summary>
    /// <remarks>
    /// One resolution per site, and nothing is held between calls. How many run at once is the
    /// caller walking the library to bound; a bound here would be a second one no reader could see.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The composition reached a metadata source issuing no site number at all. Answered as a site
    /// with no number, it would report every studio in the library as unidentified.
    /// </exception>
    Task<WhisparrSiteNumber> ResolveSiteNumberAsync(string storedSiteId, CancellationToken ct);
}
