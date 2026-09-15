using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Whisparr2.Net.Client;
using V2Api = Whisparr2.Net.Api;

namespace WhisparrSync.Whisparr;

/// <summary>Answers a site number out of the instance's own lookup.</summary>
/// <remarks>
/// The instance's lookup takes the identifier the library holds - a provider's own code as readily
/// as a number - and answers the site it names. Resolving through it rather than through Cove's
/// configured metadata source spends one request where there were two, and leaves the run depending
/// on the instance it is already talking to rather than on a third party it does not otherwise need
/// to reach.
/// <para>
/// A library whose studios are held under a provider's codes made the source read mandatory: every
/// identifier missed the numeric path, so every count paid a source request per studio and raised
/// where the source was unreachable. Neither is true of a lookup the instance answers.
/// </para>
/// </remarks>
internal sealed class InstanceSiteNumberPort(Whisparr2Gateway v2Gateway) : ISiteNumberPort
{
    /// <summary>The member the lookup names a site's own number under.</summary>
    private const string SiteNumberMember = "tvdbId";

    /// <inheritdoc/>
    public async Task<WhisparrSiteNumber> ResolveSiteNumberAsync(
        Uri baseAddress, string apiKey, string storedSiteId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(storedSiteId);

        // A library reaching this generation may already hold the number itself, and a lookup to
        // confirm a number already in hand is a request paid per studio for nothing.
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
            // Nothing whole arrived, which establishes nothing about the site. Reported as a site
            // the instance names no number for, it would offer a registered studio for registration
            // again on the strength of a request that never landed.
            return WhisparrSiteNumber.NotReached;
        }

        var reading = Whisparr2Gateway.Answered(answered);
        if (reading.StatusCode is < 200 or > 299)
        {
            return WhisparrSiteNumber.NotReached;
        }

        return NumberIn(reading.Body);
    }

    /// <summary>The number the first site <paramref name="body"/> names carries, or why none does.</summary>
    /// <remarks>
    /// The lookup orders by its own relevance and this product asks by an exact identifier, so the
    /// first entry is the site asked about. An answer carrying no entry is the instance stating it
    /// names no site for the identifier, which is not the same as not having answered.
    /// </remarks>
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
