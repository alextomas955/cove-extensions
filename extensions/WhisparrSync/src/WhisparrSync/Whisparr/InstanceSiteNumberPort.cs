using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Whisparr2.Net.Client;
using V2Api = Whisparr2.Net.Api;

namespace WhisparrSync.Whisparr;

// v2's lookup takes the identifier the library holds, a provider's own code as readily as a number,
// and answers the site it names. Cove's configured metadata source is not asked.
internal sealed class InstanceSiteNumberPort(Whisparr2Gateway v2Gateway) : ISiteNumberPort
{
    // v2's lookup carries a site's own number under tvdbId.
    private const string SiteNumberMember = "tvdbId";

    public async Task<WhisparrSiteNumber> ResolveSiteNumberAsync(
        Uri baseAddress, string apiKey, string storedSiteId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(storedSiteId);

        // The library may already hold the number itself, and confirming it costs a request per
        // studio for nothing.
        if (int.TryParse(storedSiteId, NumberStyles.None, CultureInfo.InvariantCulture, out var held)
            && held > 0)
        {
            return WhisparrSiteNumber.Numbered(held);
        }

        IApiResponse answered;
        try
        {
            answered = await v2Gateway
                .For(new Whisparr2Target(baseAddress, apiKey))
                .Api<V2Api.ISeriesLookupApi>()
                .ListSeriesLookupAsync(storedSiteId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            // Nothing arrived, which establishes nothing about the site. Reported as "no number",
            // it would offer an already registered studio for registration again.
            return WhisparrSiteNumber.NotReached;
        }

        var reading = Whisparr2Gateway.Answered(answered);
        if (reading.StatusCode is < 200 or > 299)
        {
            return WhisparrSiteNumber.NotReached;
        }

        return NumberIn(reading.Body);
    }

    // The lookup orders by its own relevance and the ask is an exact identifier, so the first entry
    // is the site asked about. An answer carrying no entry is the instance naming no site, which is
    // not the same as not having answered.
    private static WhisparrSiteNumber NumberIn(string body)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return WhisparrSiteNumber.NotReached;
        }

        if (parsed is not JsonArray sites)
        {
            return WhisparrSiteNumber.NotReached;
        }

        if (sites.Count == 0)
        {
            return WhisparrSiteNumber.NamesNone;
        }

        return sites[0] is JsonObject site
            && site[SiteNumberMember] is JsonValue number
            && number.TryGetValue<int>(out var named)
            && named > 0
                ? WhisparrSiteNumber.Numbered(named)
                : WhisparrSiteNumber.NamesNone;
    }
}
