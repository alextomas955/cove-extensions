using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Library;

// The request counts are the point of this path: a page of cards is one read of the instance, and a
// card the batch could not speak for is the only one asked about on its own.
public sealed class LibraryStatusBatchTests
{
    private const string Held = "5ee16943-0da6-4ee4-94c1-54172e3d0b7e";
    private const string Absent = "0f0c0a8e-6e2f-4a2e-9f6e-2a1f0a3b4c5d";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APageOfEntityCardsCostsOneRead()
    {
        var batch = new RecordingEntityBatch(Answering(Held));
        var perCard = new CountingRead();

        var rows = await ReadEntitiesAsync(batch, perCard, [1, 2, 3]);

        Assert.Equal(1, batch.Calls);
        Assert.Equal(0, perCard.Calls);
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task AnEntityTheBatchNamesIsPresentAndCarriesItsMonitoredFlag()
    {
        var batch = new RecordingEntityBatch(Answering(Held, monitored: true));

        var rows = await ReadEntitiesAsync(batch, new CountingRead(), [1]);

        Assert.Equal(new LibraryCardReading(false, true, true), rows[0].Reading);
    }

    // An identifier the answer carries no row for is the instance stating it holds none, which is a
    // different fact from one it could not speak for.
    [Fact]
    public async Task AnEntityTheBatchDoesNotNameIsAbsent()
    {
        var batch = new RecordingEntityBatch(WhisparrHeldCards.Empty);

        var rows = await ReadEntitiesAsync(batch, new CountingRead(), [1]);

        Assert.Equal(new LibraryCardReading(false, false, null), rows[0].Reading);
    }

    // One generation addresses a site by a number it issues itself, so an identifier that is not one
    // is outside what its list answers and is asked about on its own.
    [Fact]
    public async Task AnIdentifierTheBatchCannotSpeakForIsAskedAboutOnItsOwn()
    {
        var batch = new RecordingEntityBatch(
            new WhisparrHeldCards(
                new Dictionary<string, WhisparrHeldCard>(StringComparer.Ordinal),
                new HashSet<string>([Held], StringComparer.Ordinal)));
        var perCard = new CountingRead(status: 200, body: """{"monitored":true}""");

        var rows = await ReadEntitiesAsync(batch, perCard, [1]);

        Assert.Equal(1, batch.Calls);
        Assert.Equal(1, perCard.Calls);
        Assert.Equal(new LibraryCardReading(false, true, true), rows[0].Reading);
    }

    // A batch that did not answer reports the page as unestablished rather than sending a read per
    // card behind it: the page would then cost what the batch was there to avoid.
    [Fact]
    public async Task AContainedBatchLeavesNoPerCardReadsBehindIt()
    {
        var batch = new RecordingEntityBatch(failure: () => new HttpRequestException("dropped"));
        var perCard = new CountingRead();

        var read = await new LibraryStatusPort(Resolving, NullLogger.Instance)
            .ReadEntityCardsAsync(
                perCard.AnswerAsync,
                new Capability<IWhisparrEntityBatchReading>(batch, null),
                WhisparrEntityKind.Studio,
                WhisparrGeneration.V3,
                Instance,
                ApiKey,
                [1, 2],
                TestCt);

        Assert.True(read.AnyReadDropped);
        Assert.Equal(0, perCard.Calls);
        Assert.All(read.Rows, row => Assert.Equal(new LibraryCardReading(false, null, null), row.Reading));
    }

    [Fact]
    public async Task ACardNamingNoIdentifierCostsNothingAndIsNotAsked()
    {
        var batch = new RecordingEntityBatch(WhisparrHeldCards.Empty);

        var read = await new LibraryStatusPort(Unmatched, NullLogger.Instance)
            .ReadEntityCardsAsync(
                new CountingRead().AnswerAsync,
                new Capability<IWhisparrEntityBatchReading>(batch, null),
                WhisparrEntityKind.Studio,
                WhisparrGeneration.V3,
                Instance,
                ApiKey,
                [1],
                TestCt);

        Assert.Equal(0, batch.Calls);
        Assert.Null(read.Rows[0].Reading);
    }

    [Fact]
    public async Task APageOfSceneCardsCostsOneReadAndKeepsTheFileFlag()
    {
        var batch = new RecordingSceneBatch(
            new WhisparrHeldCards(
                new Dictionary<string, WhisparrHeldCard>(StringComparer.Ordinal)
                {
                    [Held] = new WhisparrHeldCard(true, true),
                },
                new HashSet<string>(StringComparer.Ordinal)));

        var readings = await ReadScenesAsync(batch, [Held, Absent]);

        Assert.Equal(1, batch.Calls);
        Assert.Equal(new LibraryCardReading(false, true, true, true), readings[1]);
        Assert.Equal(new LibraryCardReading(false, false, null, false), readings[2]);
    }

    private static WhisparrHeldCards Answering(string foreignId, bool monitored = false)
        => new(
            new Dictionary<string, WhisparrHeldCard>(StringComparer.Ordinal)
            {
                [foreignId] = new WhisparrHeldCard(monitored, null),
            },
            new HashSet<string>(StringComparer.Ordinal));

    private static async Task<IReadOnlyList<LibraryStatusRow>> ReadEntitiesAsync(
        RecordingEntityBatch batch, CountingRead perCard, IReadOnlyList<int> coveIds)
        => (await new LibraryStatusPort(Resolving, NullLogger.Instance)
            .ReadEntityCardsAsync(
                perCard.AnswerAsync,
                new Capability<IWhisparrEntityBatchReading>(batch, null),
                WhisparrEntityKind.Studio,
                WhisparrGeneration.V3,
                Instance,
                ApiKey,
                coveIds,
                TestCt)).Rows;

    private static async Task<IReadOnlyDictionary<int, LibraryCardReading>> ReadScenesAsync(
        RecordingSceneBatch batch, IReadOnlyList<string> remoteIds)
        => (await new LibraryStatusPort(Unmatched, NullLogger.Instance)
            .ReadSceneCardsAsync(
                new RefusingSceneReading(),
                GenerationCapabilities.For(WhisparrGeneration.V2)
                    .Obtain<IWhisparrSceneExclusionReading>(),
                new Capability<IWhisparrSceneBatchReading>(batch, null),
                Instance,
                ApiKey,
                WhisparrGeneration.V3,
                [.. remoteIds.Select((id, index) => new LibraryCardIdentity(index + 1, id))],
                TestCt)).Readings;

    private static Uri Instance => new("http://whisparr.invalid");

    private static string ApiKey => "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    private static IEntityIdentityPort Resolving => new FixedIdentities(IdentityResolution.At(Held));

    private static IEntityIdentityPort Unmatched => new FixedIdentities(IdentityResolution.Unmatched);

    private sealed class FixedIdentities(IdentityResolution answer) : IEntityIdentityPort
    {
        public Task<IdentityResolution> ResolveAsync(
            WhisparrEntityKind kind, int coveId, WhisparrGeneration generation, CancellationToken ct)
            => Task.FromResult(answer);
    }

    private sealed class RecordingEntityBatch(
        WhisparrHeldCards? answer = null, Func<Exception>? failure = null)
        : IWhisparrEntityBatchReading
    {
        public int Calls { get; private set; }

        public Task<WhisparrHeldCards> ReadHeldEntitiesAsync(
            Uri baseAddress,
            string apiKey,
            WhisparrGeneration generation,
            WhisparrEntityKind kind,
            IReadOnlyList<string> foreignIds,
            CancellationToken ct)
        {
            Calls++;
            return failure is null
                ? Task.FromResult(answer!)
                : Task.FromException<WhisparrHeldCards>(failure());
        }
    }

    private sealed class RecordingSceneBatch(WhisparrHeldCards answer) : IWhisparrSceneBatchReading
    {
        public int Calls { get; private set; }

        public Task<WhisparrHeldCards> ReadHeldSceneCardsAsync(
            Uri baseAddress, string apiKey, IReadOnlyList<string> foreignIds, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(answer);
        }
    }

    // The batch answers for every scene on these pages, so a per-scene read is a defect and refuses
    // rather than answering something a case could pass on.
    private sealed class RefusingSceneReading : IWhisparrSceneStatusReading
    {
        public Task<WhisparrResponse> ReadEntityPresenceAsync(
            Uri baseAddress,
            string apiKey,
            WhisparrEntityKind kind,
            string foreignId,
            CancellationToken ct)
            => throw new InvalidOperationException("The scene card path probes no entity.");

        public Task<WhisparrResponse> ReadSceneByRemoteIdAsync(
            Uri baseAddress, string apiKey, string remoteId, CancellationToken ct)
            => throw new InvalidOperationException(
                "The batch answered for this page, so no scene is read on its own.");

        public Task<IReadOnlySet<string>> ReduceHeldScenesAsync(
            Uri baseAddress,
            string apiKey,
            IReadOnlyCollection<string> foreignIds,
            CancellationToken ct)
            => throw new InvalidOperationException(
                "The card path reads statuses, not which of a set the instance holds.");
    }

    private sealed class CountingRead(int status = 200, string body = "[]")
    {
        public int Calls { get; private set; }

#pragma warning disable IDE0060, S1172 // The delegate this stands in for takes both.
        public Task<WhisparrResponse> AnswerAsync(string foreignId, CancellationToken ct)
#pragma warning restore IDE0060, S1172
        {
            Calls++;
            return Task.FromResult(new WhisparrResponse(status, "application/json", body));
        }
    }
}
