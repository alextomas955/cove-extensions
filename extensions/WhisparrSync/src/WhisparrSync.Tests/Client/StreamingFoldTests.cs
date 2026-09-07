using System.Net;
using System.Net.Http.Headers;
using System.Text;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.SceneStatus;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Client;

/// <summary>
/// The transport half of the streamed whole-library read: what happens when the body — which is now consumed
/// after the send call returns rather than inside it — fails, stalls, or is read a second time on a retry.
/// Every case here is one the buffered read could not produce, which is why they are asserted rather than
/// assumed to be covered by the existing send-loop tests.
/// </summary>
[Trait("Tier", "L0")]
public sealed class StreamingFoldTests
{
    private const string BaseUrl = "http://whisparr.test";
    private const string ApiKey = "key";

    private static readonly string[] RowsJson =
    [
        """{ "stashId": "stash-a", "foreignId": "stash-a", "itemType": "scene", "monitored": true, "hasFile": false }""",
        """{ "stashId": "stash-b", "foreignId": "999001", "itemType": "movie", "monitored": false, "hasFile": true }""",
        """{ "stashId": "stash-c", "foreignId": "stash-c", "itemType": "scene", "monitored": false, "hasFile": false }""",
    ];

    private static readonly string ThreeRows = "[" + string.Join(",", RowsJson) + "]";

    // How many bytes of that body carry the first <paramref name="rows"/> elements and the comma after them,
    // so a truncation can be placed at an element boundary rather than at a guessed offset.
    private static int BytesThrough(int rows)
        => Encoding.UTF8.GetByteCount("[" + string.Join(",", RowsJson.Take(rows)) + ",");

    private static HttpResponseMessage Streaming(Stream body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(status) { Content = content };
    }

    private static MemoryStream Utf8(string body) => new(Encoding.UTF8.GetBytes(body));

    private static Task<WhisparrResult<Dictionary<string, WhisparrMovieFacts>>> FoldAsync(
        FakeHttpMessageHandler handler, CancellationToken ct = default)
        => new WhisparrClient(new HttpClient(handler)).FoldMovieFactsAsync(
            BaseUrl,
            ApiKey,
            () => new Dictionary<string, WhisparrMovieFacts>(StringComparer.OrdinalIgnoreCase),
            (index, movie) =>
            {
                SceneStatusProjector.IndexMovie(index, movie);
                return index;
            },
            ct);

    [Fact]
    public async Task The_fold_indexes_every_row_and_asks_whisparr_to_leave_out_the_local_covers()
    {
        var handler = FakeHttpMessageHandler.Sequence(() => Streaming(Utf8(ThreeRows)));

        var result = await FoldAsync(handler);

        Assert.True(result.IsOk);
        // Three keys, not four: the movie-typed row's foreignId is a tmdbId, so it is not a StashDB key.
        Assert.Equal(
            ["stash-a", "stash-b", "stash-c"],
            result.Value!.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("excludeLocalCovers=true", handler.LastRequest!.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_parse_failure_part_way_through_the_body_classifies_rather_than_escaping()
    {
        // Well-formed up to the third element, then garbage — the shape a buffered read could never produce,
        // because it would have failed before a single element was handed over.
        var handler = FakeHttpMessageHandler.Sequence(() => Streaming(Utf8(
            """
            [
              { "stashId": "stash-a", "itemType": "scene", "monitored": true, "hasFile": false },
              { "stashId": "stash-b", "itemType": "scene", "monitored": true, "hasFile": false },
              { "stashId": ] not json at all
            """)));

        var result = await FoldAsync(handler);

        Assert.Equal(WhisparrResultState.NotWhisparr, result.State);
        // Not a partially-folded success: the two rows that DID parse must not be reported as the answer.
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task A_transport_failure_part_way_through_the_body_classifies_as_unreachable()
    {
        // HttpIOException derives from IOException and NOT from HttpRequestException, so the send loop's
        // request-exception catch never sees it. Uncaught it escapes the classify-not-throw boundary as a 500.
        // Both attempts die, so the classification is what is left rather than a retry's success.
        var handler = FakeHttpMessageHandler.Sequence(() => Streaming(DiesAfterTwoRows()));

        var result = await FoldAsync(handler);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
    }

    [Fact]
    public async Task A_retried_attempt_folds_from_a_fresh_accumulator()
    {
        // The first attempt must fold PART of the body before dying, or a shared accumulator would never be
        // written to twice and the test would pass for the wrong reason. The accumulator COUNTS rows rather
        // than keying them, for the same reason: a dictionary absorbs a double fold and reports three keys
        // either way.
        var handler = FakeHttpMessageHandler.Sequence(
            () => Streaming(DiesAfterTwoRows()),
            () => Streaming(Utf8(ThreeRows)));

        var result = await new WhisparrClient(new HttpClient(handler)).FoldMovieFactsAsync(
            BaseUrl,
            ApiKey,
            () => new List<WhisparrMovieFacts>(),
            (rows, movie) =>
            {
                rows.Add(movie);
                return rows;
            },
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(3, result.Value!.Count);
    }

    [Fact]
    public async Task The_body_is_read_through_the_stall_budget_and_not_through_the_callers_own_token()
    {
        var body = new TokenRecordingStream(Encoding.UTF8.GetBytes(ThreeRows));
        var handler = FakeHttpMessageHandler.Sequence(() => Streaming(body));
        using var caller = new CancellationTokenSource();

        var result = await FoldAsync(handler, caller.Token);

        Assert.True(result.IsOk);
        Assert.NotEmpty(body.ObservedTokens);
        // A read that saw the caller's token verbatim is a read with no stall budget on it: the wrapper arms a
        // FRESH budget per read, so every read is handed a token of its own, linked to the caller's.
        Assert.DoesNotContain(caller.Token, body.ObservedTokens);
        Assert.Equal(body.ObservedTokens.Count, body.ObservedTokens.Distinct().Count());
    }

    [Fact]
    public async Task A_stalled_read_is_abandoned_by_the_budget_rather_than_by_the_callers_token()
    {
        using var caller = new CancellationTokenSource();
        await using var stalling = new StallingStream();
        await using var bounded = new IdleReadTimeoutStream(stalling, TimeSpan.FromMilliseconds(80));

        var buffer = new byte[16];
        var failure = await Assert.ThrowsAsync<IOException>(async () =>
        {
            var read = await bounded.ReadAsync(buffer.AsMemory(), caller.Token);
            Assert.Fail($"the stalled read returned {read} byte(s) instead of being abandoned");
        });

        Assert.Contains("stalled", failure.Message, StringComparison.Ordinal);
        // The distinction the classification turns on: an IOException is a dead transfer the send loop retries
        // and classifies, whereas the caller's own cancellation must propagate untouched.
        Assert.IsNotType<OperationCanceledException>(failure);
        Assert.False(caller.IsCancellationRequested);
    }

    [Fact]
    public async Task The_callers_own_cancellation_still_reaches_the_inner_read()
    {
        using var caller = new CancellationTokenSource();
        await using var stalling = new StallingStream();
        await using var bounded = new IdleReadTimeoutStream(stalling, TimeSpan.FromSeconds(30));

        var buffer = new byte[16];
        var reading = bounded.ReadAsync(buffer.AsMemory(), caller.Token).AsTask();
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reading);
    }

    [Fact]
    public void Only_the_generation_whose_rows_carry_a_scene_id_carries_the_status_index_role()
    {
        var client = new WhisparrClient(new HttpClient(FakeHttpMessageHandler.Json("[]")));

        // The role is this generation's alone: the index is keyed by the scene-level ids a Cove scene carries, and
        // the older generation's synthesized rows carry none — so the index it could build is empty for a library
        // of any size. The response-body fold tested in this class is therefore also the only one a shipped status
        // read reaches.
        Assert.IsAssignableFrom<IWhisparrStatusIndexSource>(new V3Adapter(client));
        Assert.IsNotAssignableFrom<IWhisparrStatusIndexSource>(new V2Adapter(client));
    }

    // A body that hands over its first two elements and then dies, so a failure — and a retry — lands
    // part-way through the fold rather than before it starts.
    private static FailingStream DiesAfterTwoRows()
        => new(
            Encoding.UTF8.GetBytes(ThreeRows),
            BytesThrough(2),
            () => new HttpIOException(HttpRequestError.ResponseEnded, "the connection died mid-body"));

    // Serves its bytes and then fails, so the failure lands part-way through the body rather than before it.
    private sealed class FailingStream(byte[] bytes, int failAfterBytes, Func<Exception> failure) : ReadOnlyStream
    {
        private int _position;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= failAfterBytes)
            {
                throw failure();
            }

            var take = Math.Min(buffer.Length, Math.Min(failAfterBytes, bytes.Length) - _position);
            bytes.AsSpan(_position, take).CopyTo(buffer.Span);
            _position += take;
            return ValueTask.FromResult(take);
        }
    }

    // Records the token each read was handed, which is how "the body is read through the budget" is checked
    // without waiting for a budget to expire.
    private sealed class TokenRecordingStream(byte[] bytes) : ReadOnlyStream
    {
        private int _position;

        public List<CancellationToken> ObservedTokens { get; } = [];

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObservedTokens.Add(cancellationToken);
            var take = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsSpan(_position, take).CopyTo(buffer.Span);
            _position += take;
            return ValueTask.FromResult(take);
        }
    }

    // Never returns anything, so only a budget or a cancellation can end the read.
    private sealed class StallingStream : ReadOnlyStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private abstract class ReadOnlyStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
