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

// A connection failure reading as unreachable can only be settled against a real socket, so the
// cases below open one against a port nothing listens on and a name that cannot resolve. The client
// is built with the settings it ships with, so the timeout, the redirect cap and the certificate
// policy under test are the ones a user gets. The composed request, the retry decision and the
// returned content type are read off a stub handler instead, because a real socket answers with
// nothing that says what was sent.
//
// The read bound holds however the client was constructed, so the cases driving it build their own
// HttpClient and none calls Configure. The attempt bound is the client's own timeout, so that case
// sets a short one rather than waiting out the shipped number.
public sealed class WhisparrClientTransportTests
{
    // Synthetic and authorises nothing: no instance is reached at either address below.
    private const string SomeKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    [Fact]
    public async Task AClosedPort_ReachesTheUnreachableKind()
    {
        var view = await TestAsync($"http://127.0.0.1:{ClosedLoopbackPort()}");

        Assert.Equal(ConnectionFailureKind.Unreachable, view.Kind);
        Assert.Null(view.Version);
    }

    // The .invalid top-level domain is reserved as permanently unresolvable, so the resolver has
    // one right answer here.
    [Fact]
    public async Task AnUnresolvableHost_ReachesTheUnreachableKind()
    {
        var view = await TestAsync("http://no-such-whisparr-host.invalid:6969");

        Assert.Equal(ConnectionFailureKind.Unreachable, view.Kind);
        Assert.Null(view.Version);
    }

    // No request is made for these addresses, so nothing was unreachable and the answer is that
    // the connection is not configured.
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

    // The tester's own check keeps this case unreachable from the route, so it drives the client
    // directly.
    [Fact]
    public async Task TheClientItselfRefusesASchemeItCannotOpen()
    {
        using var http = NewHttpClient();
        var transport = TestWhisparrClient.TransportOver(http);

        await Assert.ThrowsAsync<ArgumentException>(
            () => transport.ReadStatusAsync(
                new Uri("file:///etc/passwd"), SomeKey, TestContext.Current.CancellationToken));
    }

    // The count is transcribed by hand. Computed from the interface it would agree with any
    // widening, which is the event it exists to report.
    [Fact]
    public void TheSeamDeclaresTheMethodsItIsPinnedAt()
        => Assert.Equal(8, typeof(IWhisparrClient).GetMethods().Length);

    // A call site can express only the requests the seam itself declares, so none can make the
    // instance search for or download anything.
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

        // Named so the assertion above cannot pass by there being no parameter of that shape at all.
        // An implementation is bound to one instance, so the address and the key are not here.
        Assert.Contains(parameters, parameter => parameter.Name == "body");
        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(Uri));
    }

    [Fact]
    public void TheReadClassRetriesAndAnUnlistedClassDoesNot()
    {
        Assert.Equal(2, WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Read));
        Assert.Equal(WhisparrRetryPolicy.NoRetry, WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Configure));
        Assert.Equal(WhisparrRetryPolicy.NoRetry, WhisparrRetryPolicy.AttemptsFor((WhisparrVerbClass)(-1)));
    }

    // The send bounds itself with the value the client carries. Without this case the constant
    // could stop reaching the client and the bound would become the framework default in silence.
    [Fact]
    public void TheShippedTimeoutIsTheNumberAnAttemptIsBoundedBy()
    {
        using var configured = new HttpClient();

        WhisparrTransport.Configure(configured);

        Assert.Equal(WhisparrTransport.RequestTimeout, configured.Timeout);
    }

    // The value the bound must be below is read off a client nothing configured, so a constant
    // raised to what the framework already allows fails here rather than passing against itself.
    [Fact]
    public void TheBoundOnOneAnswerIsANarrowing()
    {
        using var unconfigured = new HttpClient();

        Assert.True(
            WhisparrTransport.MaxResponseBytes < unconfigured.MaxResponseContentBufferSize,
            $"the bound is {WhisparrTransport.MaxResponseBytes}, which bounds nothing a client nothing "
                + $"configured would not already refuse at {unconfigured.MaxResponseContentBufferSize}");
    }

    // The empty body is asserted because the value the bound was passed reading is the value that
    // must not travel onward. Run per generation, because each composes its request through a
    // generated client of its own and the bound is attached to each registration separately.
    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public async Task AnAnswerLargerThanThisClientWillReadIsRefusedAsThatRatherThanAsTheInstanceRefusing(
        WhisparrGeneration generation)
    {
        var handler = BodyRecordingHandler.AnsweringPastTheReadBound();
        using var http = new HttpClient(handler);

        var answered = await ReadThroughAsync(handler, generation);

        Assert.Equal(
            MonitorRefusalKind.AnswerTooLargeToRead, MonitoringProjector.Classify(answered).Refusal);
        Assert.Equal(
            MonitoringProjector.EntityReading.Refused, MonitoringProjector.Classify(answered).Reading);
        Assert.Empty(answered.Body);
    }

    // The expected value is the literal the handler was given, not anything computed from the
    // client. The title carries characters outside ASCII, which a read counting bytes as characters
    // gets wrong.
    [Fact]
    public async Task AnAnswerInsideTheBoundIsReturnedWhole()
    {
        const string sent = """[{"id":1,"title":"Vixen Mélodie","quality":"WEBDL-1080p"}]""";
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, sent);
        using var http = new HttpClient(handler);

        var answered = await ReadThroughAsync(handler);

        Assert.Equal(sent, answered.Body);
        Assert.Equal(MonitorRefusalKind.None, answered.Refusal);
    }

    // The read class carries more than one attempt, and an attempt is re-issued only where the send
    // reached nothing. An answer past the bound is an answer.
    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public async Task AnAnswerLargerThanTheBoundIsReadOnce(WhisparrGeneration generation)
    {
        var handler = BodyRecordingHandler.AnsweringPastTheReadBound();
        using var http = new HttpClient(handler);

        await ReadThroughAsync(handler, generation);

        Assert.Single(handler.Requests);
    }

    // Which type is raised decides which filters contain it, so it is read off a real socket. The
    // framework reports a stream that ended before its declared length as HttpIOException, which
    // derives from IOException and not from HttpRequestException. A filter naming only the request
    // type would let this reach a route whose declared results hold no failure.
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

    // The client asks for the headers and reads the body itself, so the framework's own timeout is
    // satisfied before the body phase begins and a stalled body would otherwise end when the server
    // gives up. The bound is read as an elapsed time, because a property would agree with itself.
    // TimeoutException as the cause is what tells this bound from the caller's own token, and
    // TaskCanceledException as the type is what carries it into the unreachable classification.
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

    // A framework-level string read skips the encoding preamble before decoding; Encoding.GetString
    // does not. Without the skip the body begins U+FEFF and every projector that parses it answers
    // null, which reads as the entity being absent with nothing saying why.
    [Fact]
    public async Task AnAnswerCarryingAByteOrderMarkIsDecodedWithoutIt()
    {
        const string sent = """[{"id":1,"title":"Vixen Mélodie"}]""";
        var handler = BodyRecordingHandler.AnsweringWithAByteOrderMarkAhead(sent);
        using var http = new HttpClient(handler);

        var answered = await ReadThroughAsync(handler);

        Assert.Equal(sent, answered.Body);
        Assert.NotNull(JsonNode.Parse(answered.Body));
    }

    // A proxy subpath is the case relative composition gets wrong: a base whose path does not end
    // in a separator drops its last segment, which would aim the request at the site root.
    // Each query is transcribed by hand from an instance of that generation answering it, rather
    // than composed from the client's own constants.
    [Theory]
    [InlineData(WhisparrGeneration.V3, "?page=2&pageSize=50&sortKey=date&sortDirection=descending&includeMovie=true")]
    [InlineData(WhisparrGeneration.V2, "?page=2&pageSize=50&sortKey=date&sortDirection=descending&includeEpisode=true")]
    public async Task TheHistoryReadComposesOntoASubpathAndCarriesTheKey(
        WhisparrGeneration generation, string query)
    {
        var handler = StubHandler.Answering(Answer(200, "application/json", "{}"));
        using var http = new HttpClient(handler);

        await TestWhisparrClient.Over(
                http,
                handler,
                generation: generation,
                baseAddress: new Uri("http://whisparr:6969/whisparr"),
                apiKey: SomeKey)
            .ReadHistoryAsync(2, 50, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/whisparr/api/v3/history", request.RequestUri?.AbsolutePath);
        Assert.Equal(query, request.RequestUri?.Query);
        Assert.Equal(SomeKey, Assert.Single(request.Headers.GetValues(WhisparrTransport.ApiKeyHeader)));
    }

    // An identifier sent to the other kind's route is answered with a not-found, which reads as an
    // instance that does not hold the entity.
    [Theory]
    [InlineData(WhisparrEntityKind.Studio, "/api/v3/studio/an-id")]
    [InlineData(WhisparrEntityKind.Performer, "/api/v3/performer/an-id")]
    public async Task EachEntityKindIsReadOnItsOwnRoute(WhisparrEntityKind kind, string expected)
    {
        var handler = StubHandler.Answering(Answer(200, "application/json", "{}"));
        using var http = new HttpClient(handler);

        await ((IWhisparrSceneStatusReading)TestWhisparrClient.Over(http, handler))
            .ReadEntityPresenceAsync(kind, "an-id", TestContext.Current.CancellationToken);

        Assert.Equal(expected, Assert.Single(handler.Requests).RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task AReadThatReachedNothingIsIssuedASecondTime()
    {
        var handler = StubHandler.Failing(1, Answer(200, "application/json", "[]"));
        using var http = new HttpClient(handler);

        var response = await TestWhisparrClient.Over(http, handler).ReadHistoryAsync(1,
            10,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(200, response.StatusCode);
    }

    // Re-issuing an answer that arrived would double every request an instance refuses.
    [Fact]
    public async Task AnUnwelcomeStatusIsAnAnswerAndIsNotReIssued()
    {
        var handler = StubHandler.Answering(Answer(401, null, ""));
        using var http = new HttpClient(handler);

        var response = await TestWhisparrClient.Over(http, handler).ReadHistoryAsync(1,
            10,
            TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests);
        Assert.Equal(401, response.StatusCode);
        Assert.Null(response.ContentType);
    }

    // One generation publishes no contract, so its answers are read on content type and parsed
    // shape. A header this client normalised would be a fact about this client.
    [Fact]
    public async Task TheContentTypeIsReturnedAsItWasReceived()
    {
        var handler = StubHandler.Answering(Answer(200, "application/json; charset=utf-8", "{}"));
        using var http = new HttpClient(handler);

        var response = await TestWhisparrClient.Over(http, handler).ReadHistoryAsync(1,
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
            () => TestWhisparrClient.Over(http, handler).ReadHistoryAsync(page,
                pageSize,
                TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
    }

    // Any read member reaches the same send, and this one is what the other transport cases drive.
    private static Task<WhisparrResponse> ReadThroughAsync(HttpMessageHandler handler)
        => ReadThroughAsync(handler, WhisparrGeneration.V3);

    private static Task<WhisparrResponse> ReadThroughAsync(
        HttpMessageHandler handler, WhisparrGeneration generation)
        => TestWhisparrClient.Over(handler, generation: generation, apiKey: SomeKey)
            .ReadHistoryAsync(1, 10, TestContext.Current.CancellationToken);

    private static Task<WhisparrResponse> ReadThroughAsync(HttpClient http, int port)
        => TestWhisparrClient
            .Over(http, baseAddress: new Uri($"http://127.0.0.1:{port}"), apiKey: SomeKey)
            .ReadHistoryAsync(1, 10, TestContext.Current.CancellationToken);

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
        => new(
            TestWhisparrClient.TransportOver(NewHttpClient()),
            NullLogger<ConnectionTester>.Instance);

    private static HttpClient NewHttpClient()
    {
        var http = new HttpClient(WhisparrTransport.CreateHandler());
        WhisparrTransport.Configure(http);
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

    // A real socket rather than a message handler, because these cases measure the type the
    // framework itself raises out of a response stream; a handler can only raise the type it was
    // written to raise. The connection count is the read class's attempt count, not one: a failure
    // the client re-issues after reaches a stopped listener on its second attempt.
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

    // Sits below the client and above the socket, which is the layer the composed URI, the headers
    // and the retry decision live at.
    private sealed class StubHandler(int failuresFirst, HttpResponseMessage answer) : HttpMessageHandler
    {
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
