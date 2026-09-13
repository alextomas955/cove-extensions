using WhisparrSync.Whisparr;

namespace WhisparrSync.Providers;

/// <summary>Answers a site number out of the configured metadata source.</summary>
internal sealed class SiteNumberPort(IProviderCatalogue catalogue) : ISiteNumberPort
{
    /// <inheritdoc/>
    public async Task<WhisparrSiteNumber> ResolveSiteNumberAsync(
        string storedSiteId, CancellationToken ct)
    {
        _ = await catalogue.ResolveNumericSiteIdAsync(storedSiteId, ct).ConfigureAwait(false);
        return WhisparrSiteNumber.NotReached;
    }
}
