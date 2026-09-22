using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

// Each generation's requests are composed by a generated client that stands up an HttpClient of its
// own, so the handler under test is supplied to all three. A test that supplied it to one would
// record part of the requests and assert on that part. The bound on one attempt is read off the
// supplied client for the same reason.
internal static class TestWhisparrClient
{
    public static WhisparrClient Over(
        HttpClient http,
        HttpMessageHandler? handler = null,
        ILogger? log = null,
        ISiteNumberPort? siteNumbers = null)
    {
        Func<HttpMessageHandler> primary =
            handler is null ? WhisparrTransport.CreateHandler : () => handler;
        void Timeout(HttpClient client) => client.Timeout = http.Timeout;

        return new WhisparrClient(
            new WhisparrTransport(http, log ?? NullLogger.Instance),
            new Whisparr3Gateway(primary, Timeout),
            new Whisparr2Gateway(primary, Timeout),
            siteNumbers ?? new TestSiteNumbers(),
            log ?? NullLogger.Instance);
    }

    public static WhisparrClient Over(
        HttpMessageHandler handler, ILogger? log = null, ISiteNumberPort? siteNumbers = null)
    {
        var http = new HttpClient(handler);
        WhisparrTransport.Configure(http);
        return Over(http, handler, log, siteNumbers);
    }
}

// An identifier that already parses as a positive number answers with that number and asks nothing,
// which is what the shipped port does. Every other identifier answers that the source names no site
// unless a case states otherwise, so a case that forgot to state one fails on the refusal rather
// than on an unconfigured request.
internal sealed class TestSiteNumbers : ISiteNumberPort
{
    private readonly Dictionary<string, WhisparrSiteNumber> _answers =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> Asked { get; } = [];

    public static TestSiteNumbers Numbering(string storedSiteId, int number)
        => new TestSiteNumbers().Answering(storedSiteId, WhisparrSiteNumber.Numbered(number));

    public TestSiteNumbers Answering(string storedSiteId, WhisparrSiteNumber answer)
    {
        _answers[storedSiteId] = answer;
        return this;
    }

    public Task<WhisparrSiteNumber> ResolveSiteNumberAsync(
        Uri baseAddress, string apiKey, string storedSiteId, CancellationToken ct)
    {
        Asked.Add(storedSiteId);

        if (int.TryParse(storedSiteId, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number > 0)
        {
            return Task.FromResult(WhisparrSiteNumber.Numbered(number));
        }

        return Task.FromResult(
            _answers.GetValueOrDefault(storedSiteId, WhisparrSiteNumber.NamesNone));
    }
}
