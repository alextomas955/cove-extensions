using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Library;

/// <summary>
/// What each card on a page reads as, and how many requests a page of them costs.
/// </summary>
/// <remarks>
/// The two facts a badge acts on differently are the point: an entity the instance holds no entry
/// for, and one nothing could be established about. A port collapsing them would draw a state for a
/// card nothing answered for.
/// <para>
/// The cost claim is asserted rather than the answers alone, because every value here is also
/// derivable from a shape that reads the instance's whole catalogue.
/// </para>
/// </remarks>
public sealed class LibraryStatusPortTests
{
    private const string ForeignId = "5ee16943-0da6-4ee4-94c1-54172e3d0b7e";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>
    /// A card the library names no identifier for costs no request and carries no reading.
    /// </summary>
    [Fact]
    public async Task AnUnresolvedIdentityIsNoReadingAndNoRequest()
    {
        var reading = new RecordingEntityReading(status: 200, body: Monitored);

        var rows = await ReadAsync(reading, Nothing, [1, 2]);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Null(row.Reading));
        Assert.Equal(0, reading.Calls);
    }

    /// <summary>
    /// An instance holding no entry answers an absence, which is a different fact from an entry it
    /// holds and does not monitor.
    /// </summary>
    [Fact]
    public async Task AnAbsentEntityIsNotPresentAndNotMonitored()
    {
        var reading = new RecordingEntityReading(status: 404, body: "");

        var rows = await ReadAsync(reading, Resolving, [1]);

        Assert.Equal(new LibraryCardReading(false, false, false), rows[0].Reading);
    }

    /// <summary>An entity the instance holds and monitors answers both.</summary>
    [Fact]
    public async Task AHeldAndMonitoredEntityIsPresentAndMonitored()
    {
        var reading = new RecordingEntityReading(status: 200, body: Monitored);

        var rows = await ReadAsync(reading, Resolving, [1]);

        Assert.Equal(new LibraryCardReading(false, true, true), rows[0].Reading);
    }

    /// <summary>An entity the instance holds and does not monitor answers presence alone.</summary>
    [Fact]
    public async Task AHeldAndUnmonitoredEntityIsPresentAndNotMonitored()
    {
        var reading = new RecordingEntityReading(status: 200, body: """{"monitored":false}""");

        var rows = await ReadAsync(reading, Resolving, [1]);

        Assert.Equal(new LibraryCardReading(false, true, false), rows[0].Reading);
    }

    /// <summary>
    /// An answer nothing could be established from carries both as unestablished, so the badge draws
    /// the unknown state rather than one of the four a reader would act on.
    /// </summary>
    [Fact]
    public async Task AnUnreadableAnswerEstablishesNeither()
    {
        var reading = new RecordingEntityReading(status: 500, body: "");

        var rows = await ReadAsync(reading, Resolving, [1]);

        Assert.Equal(new LibraryCardReading(false, null, null), rows[0].Reading);
    }

    /// <summary>A dropped connection is contained per card and leaves the rest of the page alone.</summary>
    [Fact]
    public async Task AConnectionThatDroppedIsOneUnestablishedCardAndNoMore()
    {
        var reading = new RecordingEntityReading(status: 200, body: Monitored, throwOnCall: 1);

        var rows = await ReadAsync(reading, Resolving, [1, 2]);

        Assert.Equal(new LibraryCardReading(false, null, null), rows[0].Reading);
        Assert.Equal(new LibraryCardReading(false, true, true), rows[1].Reading);
    }

    /// <summary>
    /// Nothing grows with the library: a page costs at most one request per card it was given, in the
    /// order it was given them.
    /// </summary>
    [Fact]
    public async Task APageCostsAtMostOneRequestPerRequestedCard()
    {
        var reading = new RecordingEntityReading(status: 200, body: Monitored);
        var requested = Enumerable.Range(1, 40).ToArray();

        var rows = await ReadAsync(reading, Resolving, requested);

        Assert.Equal(requested.Length, reading.Calls);
        Assert.Equal(requested, rows.Select(row => row.CoveId));
    }

    private static string Monitored => """{"id":7,"monitored":true}""";

    /// <summary>Every entity is named by one identifier.</summary>
    private static IEntityIdentityPort Resolving => new FakeIdentities(IdentityResolution.At(ForeignId));

    /// <summary>No entity is named at all, which is a library holding no usable link.</summary>
    private static IEntityIdentityPort Nothing => new FakeIdentities(IdentityResolution.Unmatched);

    private static async Task<IReadOnlyList<LibraryStatusRow>> ReadAsync(
        RecordingEntityReading reading, IEntityIdentityPort identities, IReadOnlyList<int> coveIds)
        => await new LibraryStatusPort(identities).ReadEntityCardsAsync(
            reading.AnswerAsync,
            WhisparrEntityKind.Studio,
            WhisparrGeneration.V3,
            coveIds,
            TestCt);

    private sealed class FakeIdentities(IdentityResolution answer) : IEntityIdentityPort
    {
        public Task<IdentityResolution> ResolveAsync(
            WhisparrEntityKind kind, int coveId, WhisparrGeneration generation, CancellationToken ct)
            => Task.FromResult(answer);
    }

    /// <summary>One answer for every card, and a count of how many were asked for.</summary>
    private sealed class RecordingEntityReading(int status, string body, int? throwOnCall = null)
    {
        public int Calls { get; private set; }

        public Task<WhisparrResponse> AnswerAsync(string foreignId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            Assert.Equal(ForeignId, foreignId);

            return Calls == throwOnCall
                ? throw new HttpRequestException("nothing answered")
                : Task.FromResult(new WhisparrResponse(status, "application/json", body));
        }
    }
}
