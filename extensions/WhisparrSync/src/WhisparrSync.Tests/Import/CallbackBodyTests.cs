using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace WhisparrSync.Tests.Import;

/// <summary>
/// The inbound route's own defences: the secret is compared before a byte of the body is read, the
/// read is bounded by this extension's own cap, and an answer discloses nothing about the
/// filesystem.
/// </summary>
/// <remarks>
/// This is the one route Cove admits without a permission, so every assertion here is about what a
/// caller who has not authenticated can reach. The recording core is what makes that answerable: a
/// path that reaches no call on it reached the ingest not at all.
/// </remarks>
public sealed class CallbackBodyTests
{
    private const string StoredSecret = "the-stored-secret";
    private const string V3UserAgent = "Whisparr/3.3.8.1097 (alpine 3.23.5)";
    private const string V2UserAgent = "Whisparr/2.2.0.231 (alpine 3.23.5)";

    [Fact]
    public async Task AWrongSecretIsRefusedWithoutTheBodyBeingRead()
    {
        var core = new RecordingImportCore();
        var body = new WatchedStream(Captured());

        var answered = await CallbackAsync(Request(body, secret: "not-the-stored-secret"), core);

        Assert.Equal(401, StatusOf(answered));
        Assert.Equal(0, body.BytesRead);
        Assert.False(body.WasRead, "the request body was read before the secret was compared");
        Assert.Empty(core.Ingested);
    }

    [Fact]
    public async Task ADeliveryPresentingNoSecretIsRefusedWithoutTheBodyBeingRead()
    {
        var core = new RecordingImportCore();
        var body = new WatchedStream(Captured());

        var answered = await CallbackAsync(Request(body, secret: null), core);

        Assert.Equal(401, StatusOf(answered));
        Assert.False(body.WasRead);
        Assert.Empty(core.Ingested);
    }

    /// <summary>
    /// Two wrong-secret deliveries at once are both refused, and neither reaches the ingest.
    /// </summary>
    /// <remarks>
    /// Concurrently rather than in sequence: the secret is read per request through a shared scope
    /// factory, and a check that admitted one of two racing callers would pass a sequential test
    /// unchanged.
    /// </remarks>
    [Fact]
    public async Task TwoConcurrentWrongSecretDeliveriesAreBothRefusedAndNeitherReachesTheIngest()
    {
        var core = new RecordingImportCore();
        await using var services = Container(core);

        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var bodies = new[] { new WatchedStream(Captured()), new WatchedStream(Captured()) };
        var answers = await Task.WhenAll(bodies.Select(async body =>
            (IResult)await global::WhisparrSync.WhisparrSync.CallbackAsync(
                Request(body, secret: "not-the-stored-secret"), scopes, NullLogger.Instance, TestCt)));

        Assert.All(answers, answered => Assert.Equal(401, StatusOf(answered)));
        Assert.All(bodies, body => Assert.False(body.WasRead));
        Assert.Empty(core.Ingested);
    }

    /// <summary>A body longer than the cap is refused without being materialised.</summary>
    /// <remarks>
    /// The bound is asserted on how much was READ, not on the answer: a handler that read the whole
    /// stream and then measured it would answer the same and have already paid the cost this cap
    /// exists to avoid.
    /// </remarks>
    [Fact]
    public async Task ABodyPastTheCapIsRefusedWithoutBeingMaterialised()
    {
        var core = new RecordingImportCore();
        var cap = global::WhisparrSync.WhisparrSync.MaxCallbackBodyBytes;
        var oversized = new byte[cap * 4];
        Array.Fill(oversized, (byte)' ');
        var body = new WatchedStream(oversized);

        // No declared length, so the cap has to be enforced by the read itself rather than by a
        // header a caller controls.
        var answered = await CallbackAsync(Request(body, StoredSecret, declareLength: false), core);

        Assert.Equal(400, StatusOf(answered));
        Assert.True(
            body.BytesRead <= cap + 1,
            $"{body.BytesRead} bytes were read against a cap of {cap}");
        Assert.Empty(core.Ingested);
    }

    /// <summary>A declared length past the cap is refused before the stream is touched at all.</summary>
    [Fact]
    public async Task ADeclaredLengthPastTheCapIsRefusedBeforeTheStreamIsTouched()
    {
        var core = new RecordingImportCore();
        var body = new WatchedStream(Captured());
        var request = Request(body, StoredSecret);
        request.Request.ContentLength = global::WhisparrSync.WhisparrSync.MaxCallbackBodyBytes + 1;

        var answered = await CallbackAsync(request, core);

        Assert.Equal(400, StatusOf(answered));
        Assert.False(body.WasRead);
        Assert.Empty(core.Ingested);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("17")]
    [InlineData("not json at all")]
    [InlineData("")]
    public async Task ABodyThatIsNotAJsonObjectIsRefused(string body)
    {
        var core = new RecordingImportCore();

        var answered = await CallbackAsync(
            Request(new WatchedStream(Encoding.UTF8.GetBytes(body)), StoredSecret), core);

        Assert.Equal(400, StatusOf(answered));
        Assert.Empty(core.Ingested);
    }

    /// <summary>A delivery from an agent this product does not manage is refused.</summary>
    /// <remarks>
    /// Where in a body to read is decided by the generation, so a delivery whose generation cannot
    /// be read has no defined reading and is refused rather than guessed at.
    /// </remarks>
    [Fact]
    public async Task ADeliveryFromAnUnrecognisedAgentIsRefused()
    {
        var core = new RecordingImportCore();

        var answered = await CallbackAsync(
            Request(new WatchedStream(Captured()), StoredSecret, userAgent: "curl/8.5.0"), core);

        Assert.Equal(400, StatusOf(answered));
        Assert.Empty(core.Ingested);
    }

    /// <summary>
    /// An authenticated import delivery reaches the ingest, and the answer names no path.
    /// </summary>
    /// <remarks>
    /// The positive control for every refusal above: without it each 401 and 400 could equally mean
    /// the handler refuses everything. The answer is asserted to disclose nothing about the
    /// filesystem, which is what keeps this route from being a probe of it.
    /// </remarks>
    [Fact]
    public async Task AnAuthenticatedImportDeliveryReachesTheIngestAndTheAnswerNamesNoPath()
    {
        var core = new RecordingImportCore();

        var answered = await CallbackAsync(
            Request(new WatchedStream(Captured()), StoredSecret), core);

        Assert.Equal(200, StatusOf(answered));
        var acknowledgement = ValueOf<ImportAcknowledgement>(answered);
        Assert.Equal(ImportEventOutcome.Accepted, acknowledgement.Outcome);
        Assert.Equal(CallbackSecretPosition.OutOfBand, acknowledgement.SecretPosition);

        var ingested = Assert.Single(core.Ingested);
        Assert.Equal(WhisparrGeneration.V3, ingested.Generation);
        Assert.Equal(3102, ingested.ReportedSize);
    }

    /// <summary>An event this product does not act on is answered without reaching the ingest.</summary>
    [Fact]
    public async Task AnEventTypeThisProductDoesNotActOnReachesNoIngest()
    {
        var core = new RecordingImportCore();
        var body = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(Captured()).Replace("\"Download\"", "\"Grab\"", StringComparison.Ordinal));

        var answered = await CallbackAsync(Request(new WatchedStream(body), StoredSecret), core);

        Assert.Equal(200, StatusOf(answered));
        Assert.Equal(ImportEventOutcome.Ignored, ValueOf<ImportAcknowledgement>(answered).Outcome);
        Assert.Empty(core.Ingested);
    }

    /// <summary>
    /// A delivery read as one generation records its secret position on THAT generation's connection.
    /// </summary>
    /// <remarks>
    /// The generation is read off the delivery's own user-agent while the settings page has the other
    /// one selected, which is the ordinary state of a user who is moving between instances. The
    /// selected generation's connection is asserted untouched, because a write that landed on it would
    /// tell the page an instance is delivering that has not.
    /// </remarks>
    [Fact]
    public async Task ADeliveryFromTheGenerationThatIsNotSelectedRecordsItsPositionOnItsOwnConnection()
    {
        var core = new RecordingImportCore();
        var options = await SeededAsync(WhisparrGeneration.V3, storeV2Connection: true);

        var answered = await CallbackAsync(
            Request(new WatchedStream(CapturedV2()), StoredSecret, userAgent: V2UserAgent),
            core,
            options);

        Assert.Equal(200, StatusOf(answered));
        Assert.Equal(WhisparrGeneration.V2, Assert.Single(core.Ingested).Generation);

        var stored = await options.LoadAsync(TestCt);
        Assert.Equal(CallbackSecretPosition.OutOfBand, stored.V2?.LastCallbackSecretPosition);
        Assert.Null(stored.V3?.LastCallbackSecretPosition);
    }

    /// <summary>
    /// A delivery for a generation with no stored connection records nothing and is still answered.
    /// </summary>
    /// <remarks>
    /// There is nothing to record a position on, and refusing the delivery over that would drop an
    /// import for the sake of a reading the settings page only shows.
    /// </remarks>
    [Fact]
    public async Task ADeliveryForAGenerationWithNoStoredConnectionRecordsNothingAndIsStillAnswered()
    {
        var core = new RecordingImportCore();
        var options = await SeededAsync(WhisparrGeneration.V3, storeV2Connection: false);

        var answered = await CallbackAsync(
            Request(new WatchedStream(CapturedV2()), StoredSecret, userAgent: V2UserAgent),
            core,
            options);

        Assert.Equal(200, StatusOf(answered));
        Assert.Single(core.Ingested);

        var stored = await options.LoadAsync(TestCt);
        Assert.Null(stored.V2);
        Assert.Null(stored.V3?.LastCallbackSecretPosition);
    }

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private static byte[] Captured()
        => Encoding.UTF8.GetBytes(ProbeFixtures.Read("whisparr-v3-3.3.8.1097-webhook-import.json"));

    private static byte[] CapturedV2()
        => Encoding.UTF8.GetBytes(ProbeFixtures.Read("whisparr-v2-2.2.0.231-webhook-import.json"));

    /// <summary>A store holding a connection per generation, with one of them selected.</summary>
    private static async Task<OptionsStore> SeededAsync(
        WhisparrGeneration selected, bool storeV2Connection)
    {
        var options = new OptionsStore(new FakeStore());
        await options.SaveAsync(
            new WhisparrSyncOptions
            {
                SelectedGeneration = selected,
                V3 = new WhisparrSyncGenerationConnection { Address = "http://whisparr-v3:6969" },
                V2 = storeV2Connection
                    ? new WhisparrSyncGenerationConnection { Address = "http://whisparr-v2:6969" }
                    : null,
            },
            TestCt);

        return options;
    }

    private static async Task<IResult> CallbackAsync(
        HttpContext http, RecordingImportCore core, OptionsStore? options = null)
    {
        await using var services = Container(core, options);
        return await global::WhisparrSync.WhisparrSync.CallbackAsync(
            http, services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, TestCt);
    }

    /// <summary>The services the handler resolves, and nothing it does not.</summary>
    private static ServiceProvider Container(RecordingImportCore core, OptionsStore? options = null)
        => new ServiceCollection()
            .AddScoped<ICallbackSecretPort>(_ => new StoredSecretPort())
            .AddScoped(_ => options ?? new OptionsStore(new FakeStore()))
            .AddSingleton<OptionsWriteGate>()
            .AddScoped<IImportCore>(_ => core)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    private static DefaultHttpContext Request(
        Stream body,
        string? secret,
        bool declareLength = true,
        string userAgent = V3UserAgent)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Post;
        http.Request.ContentType = "application/json";
        http.Request.Headers.UserAgent = userAgent;
        http.Request.Body = body;
        if (declareLength && body.CanSeek)
        {
            http.Request.ContentLength = body.Length;
        }

        if (secret is not null)
        {
            http.Request.Headers[CallbackSecret.CustomHeaderName] = secret;
        }

        return http;
    }

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(Unwrap(result)).StatusCode ?? 0;

    private static T ValueOf<T>(IResult result)
        => Assert.IsType<T>(Assert.IsAssignableFrom<IValueHttpResult>(Unwrap(result)).Value);

    /// <summary>A body that reports whether, and how far, it was read.</summary>
    /// <remarks>
    /// The ordering assertion cannot be made on the handler's answer: a handler that read the body
    /// and then compared the secret answers a wrong secret exactly as this one does.
    /// </remarks>
    private sealed class WatchedStream(byte[] contents) : Stream
    {
        private int _position;

        public int BytesRead { get; private set; }

        public bool WasRead => BytesRead > 0;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => contents.Length;

        public override long Position
        {
            get => _position;
            set => _position = (int)value;
        }

        // Written out rather than derived from MemoryStream, which routes its span read back through
        // Read(byte[], int, int) on a derived type. Two counted entry points reached by one read
        // reported twice what was taken, and a bound asserted on that number would have been reading
        // its own arithmetic.
        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = Math.Min(buffer.Length, contents.Length - _position);
            contents.AsSpan(_position, read).CopyTo(buffer);
            _position += read;
            BytesRead += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
            => new(Read(buffer.Span));

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>The one secret every request in this file is authenticated against.</summary>
    private sealed class StoredSecretPort : ICallbackSecretPort
    {
        public Task<string?> ReadAsync(CancellationToken ct) => Task.FromResult<string?>(StoredSecret);

        public Task<string> EnsureAsync(DateTimeOffset nowUtc, CancellationToken ct)
            => Task.FromResult(StoredSecret);
    }

    /// <summary>
    /// Records the candidates the ingest was asked for, so a path that reached it is visible and a
    /// path that did not is provable.
    /// </summary>
    /// <remarks>
    /// The arguments are recorded rather than a count: a count answers whether the ingest ran, and
    /// the question a refusal has to answer is what it would have been asked to do.
    /// </remarks>
    private sealed class RecordingImportCore : IImportCore
    {
        public List<ImportCandidate> Ingested { get; } = [];

        public Task<ImportOutcome> IngestAsync(ImportCandidate candidate, CancellationToken ct)
        {
            Ingested.Add(candidate);
            return Task.FromResult(ImportOutcome.Imported);
        }
    }
}
