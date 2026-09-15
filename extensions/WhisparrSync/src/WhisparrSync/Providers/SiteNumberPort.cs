using System.Globalization;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Providers;

/// <summary>Answers a site number out of the configured metadata source.</summary>
/// <remarks>
/// Implemented here rather than in the slice that declares the port, so the reference points from
/// the provider slice to the Whisparr one and never back.
/// </remarks>
internal sealed class SiteNumberPort(IProviderCatalogue catalogue) : ISiteNumberPort
{
    /// <inheritdoc/>
    public async Task<WhisparrSiteNumber> ResolveSiteNumberAsync(
        string storedSiteId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedSiteId);

        // A library reaching this generation may already hold the number itself, and a read to
        // confirm a number already in hand is a request paid per studio for nothing.
        if (int.TryParse(storedSiteId, NumberStyles.None, CultureInfo.InvariantCulture, out var held)
            && held > 0)
        {
            return WhisparrSiteNumber.Numbered(held);
        }

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
