using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.SceneStatus;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.SceneStatus;

/// <summary>
/// What the toolbar summary retains while it reads Whisparr's movie set, measured at two element counts a
/// tenfold apart, and the reason the win is a constant factor rather than an order.
/// </summary>
/// <remarks>
/// <para>
/// A memory harness reports a number whether or not it held anything, so the sampling instant is guarded
/// rather than trusted: the sample is taken while the body is still arriving AND the accumulator is already
/// populated, and both conditions are asserted at that instant with a message naming each. Swapping the lazy
/// body for a fully buffered one makes the first condition false and fails the test instead of quietly
/// reporting a smaller figure.
/// </para>
/// <para>
/// Absolute byte figures are printed rather than asserted — they are machine-specific and would make a flaky
/// gate. What is asserted is the SHAPE: that both paths grow with the movie set (which is the residual this
/// phase does NOT remove), and that the streamed narrow path retains a fraction of what the materialised one
/// does.
/// </para>
/// </remarks>
[Trait("Tier", "L0")]
public sealed class SummaryReadCostTests
{
    private const string BaseUrl = "http://whisparr.local:6969";
    private const string ApiKey = "KEY";

    // Tenfold apart, and large enough that the per-entry constant dominates the fixed overhead of a
    // dictionary and a response.
    private const int SmallCorpus = 10_000;
    private const int LargeCorpus = 100_000;

    // Where in the pass the sample is taken. Short of the end on purpose: at the last element the only thing
    // left on the wire is a closing bracket, and "the body is still arriving" would be true by one byte.
    private const double SampleAtFraction = 0.9;

    [Fact]
    public async Task The_streamed_narrow_read_retains_a_fraction_of_what_the_materialised_one_does_and_both_grow()
    {
        var streamedSmall = await MeasureStreamedAsync(SmallCorpus);
        var streamedLarge = await MeasureStreamedAsync(LargeCorpus);
        var materialisedSmall = await MeasureMaterialisedAsync(SmallCorpus);
        var materialisedLarge = await MeasureMaterialisedAsync(LargeCorpus);

        Report("streamed narrow", streamedSmall, streamedLarge);
        Report("materialised wide", materialisedSmall, materialisedLarge);

        // Tenfold the elements retains at least several times the bytes on BOTH paths. The materialised one is
        // the baseline; the streamed one is asserted the same way because that growth is the residual this
        // phase deliberately does not remove — the index is still one entry per Whisparr movie.
        Assert.True(
            materialisedLarge.RetainedBytes > materialisedSmall.RetainedBytes * 5,
            $"materialised: {materialisedSmall.RetainedBytes} B at {materialisedSmall.Entries} rows, "
                + $"{materialisedLarge.RetainedBytes} B at {materialisedLarge.Entries}");
        Assert.True(
            streamedLarge.RetainedBytes > streamedSmall.RetainedBytes * 5,
            $"streamed: {streamedSmall.RetainedBytes} B at {streamedSmall.Entries} entries, "
                + $"{streamedLarge.RetainedBytes} B at {streamedLarge.Entries}");

        // And the constant factor the phase buys, at the larger size where the fixed terms matter least.
        Assert.True(
            streamedLarge.BytesPerEntry < materialisedLarge.BytesPerEntry / 2,
            $"streamed {streamedLarge.BytesPerEntry:0.0} B/entry vs materialised "
                + $"{materialisedLarge.BytesPerEntry:0.0} B/row");
    }

    [Fact]
    public async Task The_mid_flight_guard_fires_when_the_harness_holds_the_whole_body_itself()
    {
        // The falsification: a body the harness materialised up front means the "still on the wire" claim is
        // false at every instant, so any figure taken from it describes the harness rather than the read.
        var failure = await Assert.ThrowsAnyAsync<Exception>(
            () => MeasureStreamedAsync(SmallCorpus, buffered: true));

        Assert.Contains("still arriving", failure.Message, StringComparison.Ordinal);
        // Printed so the falsification is re-observable in a run's log rather than a claim about one.
        Console.WriteLine("GUARD FIRED: " + failure.Message.Replace("\n", " | ", StringComparison.Ordinal));
    }

    [Fact]
    public void Counting_matched_ids_is_not_counting_matched_videos()
    {
        // The bounded Cove-side read that DOES exist answers "which of these ids does Cove own?" — an id-set
        // membership question. The summary counts VIDEOS. The two diverge whenever the id↔video relation is
        // not one-to-one, and this fixture makes both directions live at once.
        CoveVideo[] videos =
        [
            // One video carrying two ids that match two different movies.
            new(1, "Two ids", null, ["stash-a", "stash-b"], [], [], []),
            // One id carried by two videos.
            new(2, "Shares an id", null, ["stash-c"], [], [], []),
            new(3, "Shares the same id", null, ["stash-c"], [], [], []),
            // A second video carrying two matching ids, so the two totals cannot coincide.
            new(4, "Two more ids", null, ["stash-d", "stash-e"], [], [], []),
        ];
        var index = SceneStatusProjector.BuildMovieIndex<WhisparrMovieFacts>(
        [
            .. new[] { "stash-a", "stash-b", "stash-c", "stash-d", "stash-e" }
                .Select(id => new WhisparrMovieFacts(id, id, "scene", Monitored: true, HasFile: false)),
        ]);

        var matchedIds = videos
            .SelectMany(video => video.StashIds)
            .Where(index.ContainsKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var counts = SceneStatusProjector.SummaryCounts(videos, index, NoExclusions);
        var matchedVideos = counts.Monitored + counts.Unmonitored;

        Assert.Equal(5, matchedIds);
        Assert.Equal(4, matchedVideos);
        Assert.NotEqual(matchedIds, matchedVideos);

        // Both directions, named separately, because a single inequality could come from either one alone.
        Assert.Equal(2, videos[0].StashIds.Count(index.ContainsKey));
        Assert.Equal(2, videos.Count(video => video.StashIds.Contains("stash-c", StringComparer.OrdinalIgnoreCase)));
    }

    private sealed record Sample(int Entries, long RetainedBytes)
    {
        public double BytesPerEntry => Entries == 0 ? 0 : (double)RetainedBytes / Entries;
    }

    private static void Report(string label, Sample small, Sample large)
        => Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"RETAINED {label,-18} {small.Entries,7} -> {small.RetainedBytes,12} B ({small.BytesPerEntry,7:0.0} B/entry)"
                + $"   {large.Entries,7} -> {large.RetainedBytes,12} B ({large.BytesPerEntry,7:0.0} B/entry)"));

    // The streamed narrow path: sampled mid-fold, with the index live and the body unfinished.
    private static async Task<Sample> MeasureStreamedAsync(int rows, bool buffered = false)
    {
        var body = buffered ? new BufferedMovieArrayStream(rows) : new GeneratedMovieArrayStream(rows);
        var handler = FakeHttpMessageHandler.Sequence(() => Streaming(body));
        var client = new WhisparrClient(new HttpClient(handler));
        var sampleAt = (int)(rows * SampleAtFraction);

        var baseline = Settled();
        long retained = 0;
        var consumed = 0;

        var result = await client.FoldMovieFactsAsync(
            BaseUrl,
            ApiKey,
            () => new Dictionary<string, WhisparrMovieFacts>(StringComparer.OrdinalIgnoreCase),
            (index, movie) =>
            {
                SceneStatusProjector.IndexMovie(index, movie);
                consumed++;
                if (consumed == sampleAt)
                {
                    AssertMidFlight(body, index.Count, sampleAt);
                    retained = Settled() - baseline;
                    GC.KeepAlive(index);
                }

                return index;
            },
            CancellationToken.None);

        Assert.True(result.IsOk);
        return new Sample(sampleAt, retained);
    }

    // The materialised path over the same generated body: the whole row array bound and held.
    private static async Task<Sample> MeasureMaterialisedAsync(int rows)
    {
        var handler = FakeHttpMessageHandler.Sequence(() => Streaming(new GeneratedMovieArrayStream(rows)));
        var client = new WhisparrClient(new HttpClient(handler));

        var baseline = Settled();
        var result = await client.ListMoviesAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.True(result.IsOk);
        var retained = Settled() - baseline;
        GC.KeepAlive(result);

        // The response buffer this path also pays is transient and already released by here, so the figure is
        // a LOWER bound on its peak — which only understates the difference being measured.
        return new Sample(result.Value!.Length, retained);
    }

    // The guard the whole measurement turns on. A fold that has already run to completion roots nothing, and a
    // body the harness holds in full was never on the wire; either way a number would still be printed.
    private static void AssertMidFlight(IMeasuredBody body, int indexed, int sampleAt)
    {
        Assert.True(
            body.ProducedBytes < body.TotalBytes,
            $"the body was not still arriving at the sampling instant: produced {body.ProducedBytes} of "
                + $"{body.TotalBytes} bytes, with {indexed} entries in the index");
        Assert.True(
            indexed >= sampleAt,
            $"the accumulator was not populated at the sampling instant: {indexed} entries indexed, expected "
                + $"at least {sampleAt}; the body had produced {body.ProducedBytes} of {body.TotalBytes} bytes");
    }

    private static long Settled()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static HttpResponseMessage Streaming(Stream body)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private interface IMeasuredBody
    {
        long ProducedBytes { get; }

        long TotalBytes { get; }
    }

    // One movie row, wide enough that the narrow projection has something to leave behind: the members the
    // status projection reads, plus the ones it does not.
    private static string RowJson(int i) => string.Create(
        CultureInfo.InvariantCulture,
        $$"""{"id":{{i}},"title":"Scene number {{i}} of the generated corpus","sortTitle":"scene number {{i}}","cleanTitle":"scenenumber{{i}}","year":2026,"stashId":"10000000-0000-4000-8000-{{i:D12}}","foreignId":"10000000-0000-4000-8000-{{i:D12}}","itemType":"scene","monitored":{{(i % 2 == 0 ? "true" : "false")}},"hasFile":{{(i % 4 == 0 ? "true" : "false")}},"studioTitle":"Generated Studio {{i % 50}}","studioForeignId":"a0000000-0000-4000-8000-{{i % 50:D12}}","path":"/data/media/scenes/Generated Studio {{i % 50}}/2026-01-01 - Scene number {{i}}","rootFolderPath":"/data/media","overview":"","genres":[],"tags":[],"images":[],"performerNames":["Performer {{i % 30}}"],"performerForeignIds":["c0000000-0000-4000-8000-{{i % 30:D12}}"]}""");

    // Produces the array one row at a time, so the harness never holds the body it is claiming was never held.
    private class GeneratedMovieArrayStream : Stream, IMeasuredBody
    {
        private readonly int _rows;
        private byte[] _pending = [];
        private int _pendingOffset;
        private int _emitted;
        private bool _opened;
        private bool _closed;

        public GeneratedMovieArrayStream(int rows)
        {
            _rows = rows;

            // Counted without retaining anything, so the guard has an exact total to compare against.
            long total = 2;
            for (var i = 1; i <= rows; i++)
            {
                total += Encoding.UTF8.GetByteCount(RowJson(i)) + (i == 1 ? 0 : 1);
            }

            TotalBytes = total;
        }

        public virtual long ProducedBytes { get; protected set; }

        public long TotalBytes { get; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pendingOffset >= _pending.Length && !Advance())
            {
                return ValueTask.FromResult(0);
            }

            var take = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
            _pending.AsSpan(_pendingOffset, take).CopyTo(buffer.Span);
            _pendingOffset += take;
            ProducedBytes += take;
            return ValueTask.FromResult(take);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private bool Advance()
        {
            if (_closed)
            {
                return false;
            }

            string next;
            if (!_opened)
            {
                _opened = true;
                next = "[";
            }
            else if (_emitted < _rows)
            {
                _emitted++;
                next = _emitted == 1 ? RowJson(_emitted) : "," + RowJson(_emitted);
            }
            else
            {
                _closed = true;
                next = "]";
            }

            _pending = Encoding.UTF8.GetBytes(next);
            _pendingOffset = 0;
            return true;
        }
    }

    // The same body, materialised in full before the read starts — the shape the guard exists to reject. It
    // reports every byte as already produced because the harness holds them all, which is the condition that
    // makes any figure taken from it a statement about the harness.
    private sealed class BufferedMovieArrayStream(int rows) : GeneratedMovieArrayStream(rows)
    {
        public override long ProducedBytes => TotalBytes;
    }

    private static readonly IReadOnlySet<string> NoExclusions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
