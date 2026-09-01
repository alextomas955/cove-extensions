using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

/// <summary>
/// What this client puts on the wire, and what the seam it sits behind can express.
/// </summary>
/// <remarks>
/// Nothing in the fixture ledger ever observed a request that produced no response, so "a connection
/// failure reads as unreachable" was an assumption until these ran. They open real sockets against a
/// port nothing listens on and a name that cannot resolve, which is the only way to settle it.
/// <para>
/// The client is built with the settings it ships with, so the timeout, the redirect cap and the
/// certificate policy under test are the ones a user gets. The composed request, the retry decision
/// and the returned content type are read off a stub handler instead: a real socket answers with
/// nothing that says what was sent.
/// </para>
/// </remarks>
public sealed class WhisparrClientTransportTests
{
    // Synthetic and authorises nothing: no instance is reached at either address below.
    private const string SomeKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    /// <summary>A refused connection.</summary>
    [Fact]
    public async Task AClosedPort_ReachesTheUnreachableKind()
    {
        var view = await TestAsync($"http://127.0.0.1:{ClosedLoopbackPort()}");

        Assert.Equal(ConnectionFailureKind.Unreachable, view.Kind);
        Assert.Null(view.Version);
    }

    /// <summary>
    /// A name that cannot resolve. The <c>.invalid</c> top-level domain is reserved as
    /// permanently unresolvable, so this asks the resolver a question with one right answer.
    /// </summary>
    [Fact]
    public async Task AnUnresolvableHost_ReachesTheUnreachableKind()
    {
        var view = await TestAsync("http://no-such-whisparr-host.invalid:6969");

        Assert.Equal(ConnectionFailureKind.Unreachable, view.Kind);
        Assert.Null(view.Version);
    }

    /// <summary>
    /// An address a socket cannot be opened to is refused before any socket is opened, and reads as
    /// not configured rather than as unreachable: no request was made, so nothing was unreachable.
    /// </summary>
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://whisparr:6969")]
    [InlineData("whisparr:6969")]
    [InlineData("not an address")]
    [InlineData("")]
    public async Task AnAddressNoSocketCanBeOpenedTo_IsRefusedWithoutARequest(string address)
        => Assert.Equal(ConnectionFailureKind.NotConfigured, (await TestAsync(address)).Kind);

    [Fact]
    public async Task NoKey_IsRefusedWithoutARequest()
        => Assert.Equal(
            ConnectionFailureKind.NotConfigured,
            (await NewTester().TestAsync("http://127.0.0.1:6969", "   ", TestContext.Current.CancellationToken)).Kind);

    /// <summary>
    /// The client refuses a scheme no socket may be opened to, rather than handing it to a handler
    /// that would act on it. The tester's own check keeps this unreachable from the route, so this
    /// drives the client directly.
    /// </summary>
    [Fact]
    public async Task TheClientItselfRefusesASchemeItCannotOpen()
    {
        using var http = NewHttpClient();
        var client = new WhisparrClient(http);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.ReadStatusAsync(
                new Uri("file:///etc/passwd"), SomeKey, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The seam's method count, against a literal.
    /// </summary>
    /// <remarks>
    /// The number is transcribed by hand. Computed from the interface it would agree with any
    /// widening, which is the event it exists to report.
    /// </remarks>
    [Fact]
    public void TheSeamDeclaresTheMethodsItIsPinnedAt()
        => Assert.Equal(7, typeof(IWhisparrClient).GetMethods().Length);

    /// <summary>
    /// No method on the seam takes a path or an HTTP verb.
    /// </summary>
    /// <remarks>
    /// The narrowness the interface's remark claims, checked rather than described: a call site can
    /// express only the requests the seam itself declares, so none can make the instance search for or
    /// download anything.
    /// </remarks>
    [Fact]
    public void NoMethodOnTheSeamTakesAPathOrAVerb()
    {
        var parameters = typeof(IWhisparrClient)
            .GetMethods()
            .SelectMany(method => method.GetParameters())
            .ToList();

        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(HttpMethod));
        Assert.DoesNotContain(
            parameters,
            parameter => parameter.ParameterType == typeof(string)
                && parameter.Name?.Contains("path", StringComparison.OrdinalIgnoreCase) == true);

        // The one string every verb takes, named so the assertion above cannot pass by there being no
        // string parameter at all.
        Assert.Contains(parameters, parameter => parameter.Name == "apiKey");
    }

    /// <summary>The read class retries; a class the table does not name does not.</summary>
    /// <remarks>
    /// The history read is a read, so it inherits the retrying class rather than declaring an attempt
    /// count of its own.
    /// </remarks>
    [Fact]
    public void TheReadClassRetriesAndAnUnlistedClassDoesNot()
    {
        Assert.Equal(2, WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Read));
        Assert.Equal(WhisparrRetryPolicy.NoRetry, WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Configure));
        Assert.Equal(WhisparrRetryPolicy.NoRetry, WhisparrRetryPolicy.AttemptsFor((WhisparrVerbClass)(-1)));
    }

    /// <summary>The configured client holds a finite amount of one answer in memory.</summary>
    /// <remarks>
    /// Read off the configured client rather than off the constant, so a <c>Configure</c> that stopped
    /// applying it is reported. The second assertion is what makes the ceiling a narrowing: a client
    /// nothing configured supplies the value this one has to be below, so a constant raised to what the
    /// framework already allows fails here rather than passing against itself.
    /// </remarks>
    [Fact]
    public void TheConfiguredClientHoldsAFiniteAnswerInMemory()
    {
        using var configured = NewHttpClient();
        using var unconfigured = new HttpClient();

        Assert.Equal(WhisparrClient.MaxResponseBytes, configured.MaxResponseContentBufferSize);
        Assert.True(
            configured.MaxResponseContentBufferSize < unconfigured.MaxResponseContentBufferSize,
            $"the ceiling is {configured.MaxResponseContentBufferSize}, which bounds nothing a client "
                + $"nothing configured would not already refuse at {unconfigured.MaxResponseContentBufferSize}");
    }

    /// <summary>
    /// The history read composes onto a base carrying a proxy subpath, presents the key, and asks
    /// each lineage for its own metadata entity.
    /// </summary>
    /// <remarks>
    /// The subpath is the case relative composition gets wrong: a base whose path does not end in a
    /// separator drops its last segment, which would aim the request at the site root.
    /// <para>
    /// The query is written out per lineage rather than composed from the client's own constants: an
    /// expectation computed from the module it checks agrees with that module however wrong both are.
    /// Each spelling was transcribed by hand from an instance of that lineage answering it.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(WhisparrGeneration.V3, "?page=2&pageSize=50&sortKey=date&sortDirection=descending&includeMovie=true")]
    [InlineData(WhisparrGeneration.V2, "?page=2&pageSize=50&sortKey=date&sortDirection=descending&includeEpisode=true")]
    public async Task TheHistoryReadComposesOntoASubpathAndCarriesTheKey(
        WhisparrGeneration generation, string query)
    {
        var handler = StubHandler.Answering(Answer(200, "application/json", "{}"));
        using var http = new HttpClient(handler);

        await new WhisparrClient(http).ReadHistoryAsync(
            new Uri("http://whisparr:6969/whisparr"),
            SomeKey,
            generation,
            2,
            50,
            TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/whisparr/api/v3/history", request.RequestUri?.AbsolutePath);
        Assert.Equal(query, request.RequestUri?.Query);
        Assert.Equal(SomeKey, Assert.Single(request.Headers.GetValues(WhisparrClient.ApiKeyHeader)));
    }

    /// <summary>A read whose first attempt reached nothing is issued a second time.</summary>
    [Fact]
    public async Task AReadThatReachedNothingIsIssuedASecondTime()
    {
        var handler = StubHandler.Failing(1, Answer(200, "application/json", "[]"));
        using var http = new HttpClient(handler);

        var response = await new WhisparrClient(http).ReadHistoryAsync(
            new Uri("http://whisparr:6969"),
            SomeKey,
            WhisparrGeneration.V3,
            1,
            10,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(200, response.StatusCode);
    }

    /// <summary>
    /// An answer that arrived is returned rather than re-issued, whatever its status.
    /// </summary>
    /// <remarks>
    /// A rejected key answers once. Re-issuing it would double every request an instance refuses.
    /// </remarks>
    [Fact]
    public async Task AnUnwelcomeStatusIsAnAnswerAndIsNotReIssued()
    {
        var handler = StubHandler.Answering(Answer(401, null, ""));
        using var http = new HttpClient(handler);

        var response = await new WhisparrClient(http).ReadHistoryAsync(
            new Uri("http://whisparr:6969"),
            SomeKey,
            WhisparrGeneration.V3,
            1,
            10,
            TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests);
        Assert.Equal(401, response.StatusCode);
        Assert.Null(response.ContentType);
    }

    /// <summary>
    /// The content type is returned as received.
    /// </summary>
    /// <remarks>
    /// One generation publishes no contract, so its answers are taken on content type and parsed
    /// shape. A header this client normalised would be a fact about this client.
    /// </remarks>
    [Fact]
    public async Task TheContentTypeIsReturnedAsItWasReceived()
    {
        var handler = StubHandler.Answering(Answer(200, "application/json; charset=utf-8", "{}"));
        using var http = new HttpClient(handler);

        var response = await new WhisparrClient(http).ReadHistoryAsync(
            new Uri("http://whisparr:6969"),
            SomeKey,
            WhisparrGeneration.V3,
            1,
            10,
            TestContext.Current.CancellationToken);

        Assert.Equal("application/json; charset=utf-8", response.ContentType);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 0)]
    [InlineData(-1, 10)]
    public async Task APageOrPageSizeBelowOneIsRefusedWithoutARequest(int page, int pageSize)
    {
        var handler = StubHandler.Answering(Answer(200, "application/json", "{}"));
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new WhisparrClient(http).ReadHistoryAsync(
                new Uri("http://whisparr:6969"),
                SomeKey,
                WhisparrGeneration.V3,
                page,
                pageSize,
                TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
    }

    /// <summary>The recording double records each history call's arguments.</summary>
    /// <remarks>
    /// The double is what every assertion about a walk's page order is read off, so a double that
    /// recorded the wrong page would make those assertions agree with themselves.
    /// </remarks>
    [Fact]
    public async Task TheRecordingDoubleRecordsEachHistoryCallsArguments()
    {
        var client = RecordingWhisparrClient.Reporting("whisparr-v3-3.3.8.1097-system-status.json");
        var address = new Uri("http://whisparr:6969");

        await ((IWhisparrClient)client).ReadHistoryAsync(
            address, SomeKey, WhisparrGeneration.V3, 1, 20, TestContext.Current.CancellationToken);
        await ((IWhisparrClient)client).ReadHistoryAsync(
            address, SomeKey, WhisparrGeneration.V2, 2, 20, TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], client.Histories.Select(call => call.Page));
        Assert.Equal(
            [WhisparrGeneration.V3, WhisparrGeneration.V2],
            client.Histories.Select(call => call.Generation));
        Assert.All(client.Histories, call => Assert.Equal(20, call.PageSize));
        Assert.All(client.Histories, call => Assert.Equal(address, call.BaseAddress));
        Assert.All(client.Verbs, verb => Assert.Equal(nameof(IWhisparrClient.ReadHistoryAsync), verb));
    }

    private static async Task<ConnectionTestView> TestAsync(string address)
        => await NewTester().TestAsync(address, SomeKey, TestContext.Current.CancellationToken);

    private static HttpResponseMessage Answer(int status, string? contentType, string body)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body),
        };
        response.Content.Headers.ContentType =
            contentType is null ? null : MediaTypeHeaderValue.Parse(contentType);
        return response;
    }

    // The HttpClient outlives this call by design: it is handed to a client whose own lifetime is the
    // test's, and disposing it here would abort the request under test.
    private static ConnectionTester NewTester()
        => new ConnectionTester(new WhisparrClient(NewHttpClient()), NullLogger<ConnectionTester>.Instance);

    private static HttpClient NewHttpClient()
    {
        var http = new HttpClient(WhisparrClient.CreateHandler());
        WhisparrClient.Configure(http);
        return http;
    }

    // A port the operating system has just confirmed is free, then released. Asking for one is what
    // makes this a port nothing listens on rather than a number this file guessed at.
    private static int ClosedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// A handler recording the request it was given and answering with what a test chose.
    /// </summary>
    /// <remarks>
    /// Below the client and above the socket, which is the layer the composed URI, the headers and the
    /// retry decision live at. A real socket cannot report what was sent, and a request never made is
    /// an empty log rather than an assertion about a timeout.
    /// </remarks>
    private sealed class StubHandler(int failuresFirst, HttpResponseMessage answer) : HttpMessageHandler
    {
        /// <summary>Every request this handler was given, in order.</summary>
        public List<HttpRequestMessage> Requests { get; } = [];

        public static StubHandler Answering(HttpResponseMessage answer) => new(0, answer);

        public static StubHandler Failing(int failuresFirst, HttpResponseMessage answer)
            => new(failuresFirst, answer);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Requests.Count <= failuresFirst
                ? throw new HttpRequestException("the stub reached nothing")
                : Task.FromResult(answer);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                answer.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
