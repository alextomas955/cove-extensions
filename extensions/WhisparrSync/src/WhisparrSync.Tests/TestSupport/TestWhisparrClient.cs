using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

// Each generation's requests are composed by a generated client that stands up an HttpClient of its
// own, so the handler under test is supplied to all three: supplying it to one would record part of
// the requests and assert on that part. The bound on one attempt is read off the supplied client
// for the same reason. The answer is the instance bound to one address and key, because a role
// member takes neither.
internal static class TestWhisparrClient
{
    public static Uri Instance { get; } = new("http://whisparr:6969/");

    public const string ApiKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    public static IWhisparrClient Over(
        HttpClient http,
        HttpMessageHandler? handler = null,
        ILogger? log = null,
        ISiteNumberPort? siteNumbers = null,
        WhisparrGeneration generation = WhisparrGeneration.V3,
        Uri? baseAddress = null,
        string? apiKey = null)
        => FactoryOver(http, handler, log, siteNumbers)
            .Bound(new WhisparrBinding(
                generation, baseAddress ?? Instance, apiKey ?? ApiKey));

    public static IWhisparrClient Over(
        HttpMessageHandler handler,
        ILogger? log = null,
        ISiteNumberPort? siteNumbers = null,
        WhisparrGeneration generation = WhisparrGeneration.V3,
        Uri? baseAddress = null,
        string? apiKey = null)
    {
        var http = new HttpClient(handler);
        WhisparrTransport.Configure(http);
        return Over(http, handler, log, siteNumbers, generation, baseAddress, apiKey);
    }

    public static WhisparrInstanceFactory FactoryOver(
        HttpClient http,
        HttpMessageHandler? handler = null,
        ILogger? log = null,
        ISiteNumberPort? siteNumbers = null)
    {
        Func<HttpMessageHandler> primary =
            handler is null ? WhisparrTransport.CreateHandler : () => handler;
        void Timeout(HttpClient client) => client.Timeout = http.Timeout;

        var v3Gateway = new Whisparr3Gateway(primary, Timeout);
        return new WhisparrInstanceFactory(
            new WhisparrTransport(http, v3Gateway, log ?? NullLogger.Instance),
            v3Gateway,
            new Whisparr2Gateway(primary, Timeout),
            siteNumbers ?? new TestSiteNumbers(),
            log ?? NullLogger.Instance);
    }

    public static WhisparrTransport TransportOver(
        HttpClient http, HttpMessageHandler? handler = null, ILogger? log = null)
    {
        Func<HttpMessageHandler> primary =
            handler is null ? WhisparrTransport.CreateHandler : () => handler;
        void Timeout(HttpClient client) => client.Timeout = http.Timeout;

        return new WhisparrTransport(
            http, new Whisparr3Gateway(primary, Timeout), log ?? NullLogger.Instance);
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
        WhisparrBinding binding, string storedSiteId, CancellationToken ct)
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
