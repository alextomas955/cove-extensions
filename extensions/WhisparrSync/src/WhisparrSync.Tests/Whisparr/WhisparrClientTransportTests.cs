using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
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
/// <para>
/// The bound on how much of one answer is read holds however the client was constructed, so the cases
/// that drive it build their own <see cref="HttpClient"/> over a stub and none of them calls
/// <c>Configure</c>.
/// </para>
/// <para>
/// The bound on how LONG one attempt may take is the client's own timeout, so the case that drives it
/// sets a short one rather than waiting out the shipped number. What ties the shipped number to that
/// bound is a case of its own over <c>Configure</c>.
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
        var client = new WhisparrClient(http, NullLogger.Instance);

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
        => Assert.Equal(8, typeof(IWhisparrClient).GetMethods().Length);

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

    /// <summary>
    /// The shipped timeout constant is the number a whole attempt is bounded by.
    /// </summary>
    /// <remarks>
    /// The send bounds itself with the value the client carries, and this is where that value comes
    /// from. Without this the constant could stop reaching the client and the bound would silently
    /// become whatever the framework defaults to, leaving the constant's own summary false again and
    /// nothing saying so.
    /// </remarks>
    [Fact]
    public void TheShippedTimeoutIsTheNumberAnAttemptIsBoundedBy()
    {
        using var configured = new HttpClient();

        WhisparrClient.Configure(configured);

        Assert.Equal(WhisparrClient.RequestTimeout, configured.Timeout);
    }

    /// <summary>The bound this client reads one answer within is a narrowing.</summary>
    /// <remarks>
    /// A client nothing configured supplies the value the bound has to be below, so a constant raised
    /// to what the framework already allows fails here rather than passing against itself.
    /// </remarks>
    [Fact]
    public void TheBoundOnOneAnswerIsANarrowing()
    {
        using var unconfigured = new HttpClient();

        Assert.True(
            WhisparrClient.MaxResponseBytes < unconfigured.MaxResponseContentBufferSize,
            $"the bound is {WhisparrClient.MaxResponseBytes}, which bounds nothing a client nothing "
                + $"configured would not already refuse at {unconfigured.MaxResponseContentBufferSize}");
    }

    /// <summary>
    /// An answer larger than this client reads at once names that reason, rather than the instance
    /// refusing.
    /// </summary>
    /// <remarks>
    /// The empty body is the third assertion rather than an aside: the value the bound was passed
    /// reading is precisely the value that must not travel onward.
    /// </remarks>
    [Fact]
    public async Task AnAnswerLargerThanThisClientWillReadIsRefusedAsThatRatherThanAsTheInstanceRefusing()
    {
        var handler = BodyRecordingHandler.AnsweringPastTheReadBound();
        using var http = new HttpClient(handler);

        var answered = await ReadThroughAsync(http);

        Assert.Equal(
            MonitorRefusalKind.AnswerTooLargeToRead, MonitoringProjector.Classify(answered).Refusal);
        Assert.Equal(
            MonitoringProjector.EntityReading.Refused, MonitoringProjector.Classify(answered).Reading);
        Assert.Empty(answered.Body);
    }

    /// <summary>An answer within the bound comes back as it was sent.</summary>
    /// <remarks>
    /// The expected value is the literal the handler was given, not anything computed from the client,
    /// so a bounded read that truncated or mis-decoded is reported here. The title carries characters
    /// outside ASCII, which is what a read counting bytes as characters gets wrong.
    /// </remarks>
    [Fact]
    public async Task AnAnswerInsideTheBoundIsReturnedWhole()
    {
        const string sent = """[{"id":1,"title":"Vixen Mélodie","quality":"WEBDL-1080p"}]""";
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, sent);
        using var http = new HttpClient(handler);

        var answered = await ReadThroughAsync(http);

        Assert.Equal(sent, answered.Body);
        Assert.Equal(MonitorRefusalKind.None, answered.Refusal);
    }

    /// <summary>An answer larger than the bound is not downloaded a second time.</summary>
    /// <remarks>
    /// The read class carries more than one attempt, and an attempt is re-issued only where the send
    /// reached nothing. An answer past the bound is an answer, so it is returned on the first one.
    /// </remarks>
    [Fact]
    public async Task AnAnswerLargerThanTheBoundIsReadOnce()
    {
        var handler = BodyRecordingHandler.AnsweringPastTheReadBound();
        using var http = new HttpClient(handler);

        await ReadThroughAsync(http);

        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// A connection dropped part way through an answer raises an I/O failure, not a request one.
    /// </summary>
    /// <remarks>
    /// Which type it is decides which filters contain it, so it is read off a real socket rather than
    /// asserted from a double. The body is read out of the response stream instead of being buffered
    /// inside the send, and the framework reports a stream that ended before its declared length as
    /// <c>HttpIOException</c>, which derives from <see cref="IOException"/> and NOT from
    /// <see cref="HttpRequestException"/>. The second assertion is the one that matters: a filter
    /// naming only the request type lets this reach a route whose declared results hold no failure.
    /// </remarks>
    [Fact]
    public async Task ABodyThatEndsBeforeItsDeclaredLengthRaisesAnIoFailureRatherThanARequestFailure()
    {
        var (port, served) = Serving(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 100000\r\n\r\n",
            async stream =>
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("""[{"id":1}]"""));
                await stream.FlushAsync();
            },
            connections: WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Read));
        using var http = new HttpClient();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => ReadThroughAsync(http, port));

        Assert.IsAssignableFrom<IOException>(failure);
        Assert.False(
            failure is HttpRequestException,
            $"{failure.GetType().FullName} is an HttpRequestException, so a filter naming only that "
                + "type would still contain a truncated answer and this case proves nothing");
        await served;
    }

    /// <summary>
    /// An instance that answers its headers and then stops sending is bounded by the configured
    /// timeout, and the bound is reported the way a timeout is.
    /// </summary>
    /// <remarks>
    /// The client asks for the headers and reads the body itself, so the framework's own timeout is
    /// already satisfied by the time the body phase begins and a stalled body ends when the SERVER
    /// gives up rather than when the client does. The bound the client applies to the whole attempt
    /// is what this case reads, and it reads it as an elapsed time rather than as a property,
    /// because a property would agree with itself.
    /// <para>
    /// <see cref="TimeoutException"/> as the cause is what tells this bound from the caller's own
    /// token, and <see cref="TaskCanceledException"/> as the type is what carries it into the
    /// unreachable classification a closed port reaches.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnAnswerThatStallsAfterItsHeadersIsBoundedByTheConfiguredTimeout()
    {
        var stalling = new TaskCompletionSource();
        var (port, served) = Serving(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 100000\r\n\r\n",
            _ => stalling.Task,
            connections: 1);
        var bound = TimeSpan.FromMilliseconds(400);
        using var http = new HttpClient { Timeout = bound };
        var started = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAsync<TaskCanceledException>(
            () => ReadThroughAsync(http, port));

        started.Stop();
        Assert.IsType<TimeoutException>(failure.InnerException);
        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(5),
            $"the read ran for {started.Elapsed} against a {bound} bound, so the bound held nothing");
        stalling.SetResult();
        await served;
    }

    /// <summary>An answer carrying a byte-order mark parses.</summary>
    /// <remarks>
    /// A framework-level string read skips the encoding preamble before decoding;
    /// <c>Encoding.GetString</c> does not. The expected value is the literal the handler was given, so
    /// a read that carried the mark onward reports here. Without the skip the returned body begins
    /// <c>U+FEFF</c> and every projector that parses it answers null, which reads as the entity being
    /// absent on every page and every press with nothing saying why.
    /// </remarks>
    [Fact]
    public async Task AnAnswerCarryingAByteOrderMarkIsDecodedWithoutIt()
    {
        const string sent = """[{"id":1,"title":"Vixen Mélodie"}]""";
        var handler = BodyRecordingHandler.AnsweringWithAByteOrderMarkAhead(sent);
        using var http = new HttpClient(handler);

        var answered = await ReadThroughAsync(http);

        Assert.Equal(sent, answered.Body);
        Assert.NotNull(JsonNode.Parse(answered.Body));
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

        await new WhisparrClient(http, NullLogger.Instance).ReadHistoryAsync(
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

        var response = await new WhisparrClient(http, NullLogger.Instance).ReadHistoryAsync(
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

        var response = await new WhisparrClient(http, NullLogger.Instance).ReadHistoryAsync(
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

        var response = await new WhisparrClient(http, NullLogger.Instance).ReadHistoryAsync(
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
            () => new WhisparrClient(http, NullLogger.Instance).ReadHistoryAsync(
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

    // Any read member reaches the same send, and this one is what the other transport cases drive.
    private static Task<WhisparrResponse> ReadThroughAsync(HttpClient http)
        => new WhisparrClient(http, NullLogger.Instance).ReadHistoryAsync(
            new Uri("http://whisparr:6969"),
            SomeKey,
            WhisparrGeneration.V3,
            1,
            10,
            TestContext.Current.CancellationToken);

    private static Task<WhisparrResponse> ReadThroughAsync(HttpClient http, int port)
        => new WhisparrClient(http, NullLogger.Instance).ReadHistoryAsync(
            new Uri($"http://127.0.0.1:{port}"),
            SomeKey,
            WhisparrGeneration.V3,
            1,
            10,
            TestContext.Current.CancellationToken);

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
        => new ConnectionTester(new WhisparrClient(NewHttpClient(), NullLogger.Instance), NullLogger<ConnectionTester>.Instance);

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
    /// Serves <paramref name="connections"/> connections, writing <paramref name="head"/> and then
    /// running <paramref name="then"/> on each, and answers the port it listens on.
    /// </summary>
    /// <remarks>
    /// A real socket rather than a message handler, because what these cases measure is the type the
    /// framework itself raises out of a response stream. A handler can only raise the type it was
    /// written to raise, which would make the assertion an assertion about the test.
    /// <para>
    /// The count is the read class's attempt count, not one: a failure the client re-issues after
    /// reaches a stopped listener on its second attempt, and a refused connection raises a different
    /// type from the one under test.
    /// </para>
    /// </remarks>
    private static (int Port, Task Served) Serving(
        string head, Func<NetworkStream, Task> then, int connections)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = Task.Run(async () =>
        {
            try
            {
                for (var answered = 0; answered < connections; answered++)
                {
                    using var accepted = await listener.AcceptTcpClientAsync();
                    await using var stream = accepted.GetStream();
                    var request = new byte[8192];
                    var requested = await stream.ReadAsync(request);
                    if (requested == 0)
                    {
                        return;
                    }

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await stream.FlushAsync();
                    await then(stream);
                }
            }
            finally
            {
                listener.Stop();
            }
        });
        return (port, served);
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
