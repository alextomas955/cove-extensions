using WhisparrSync.Whisparr;

namespace WhisparrSync.Providers;

/// <summary>Answers a site number out of the configured metadata source.</summary>
/// <remarks>
/// Implemented here rather than in the slice that declares the port, so the reference points from
/// the provider slice to the Whisparr one and never back.
/// <para>
/// Every stored identifier is confirmed through the source, including one already shaped like a
/// number. A stored number the source names no site for can be registered nowhere, and answered
/// unverified it places the studio among the ones not yet there, where every sync offers it and the
/// instance declines it again. The cost is one metadata request per studio holding a number, which
/// is the request a studio holding a uuid already pays, and the resolves on the count path stay
/// behind that path's existing in-flight bound.
/// </para>
/// </remarks>
internal sealed class SiteNumberPort(IProviderCatalogue catalogue) : ISiteNumberPort
{
    /// <inheritdoc/>
    public async Task<WhisparrSiteNumber> ResolveSiteNumberAsync(
        string storedSiteId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedSiteId);

        if (catalogue.Capabilities.Obtain<IResolvesNumericSiteId>()
                .Match<IResolvesNumericSiteId?>(role => role, _ => null)
            is null)
        {
            throw new InvalidOperationException(
                "A site number resolution reached a metadata source issuing no site number.");
        }

        var answered = await catalogue.ResolveNumericSiteIdAsync(storedSiteId, ct)
            .ConfigureAwait(false);

        if (answered.Number is { } number)
        {
            return WhisparrSiteNumber.Numbered(number);
        }

        return answered.WasReached
            ? WhisparrSiteNumber.NamesNone
            : WhisparrSiteNumber.NotReached;
    }
}
