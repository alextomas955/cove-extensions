using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Library;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Jobs;

/// <summary>
/// What one count reports, what it asks the instance, and what it leaves behind when it fails.
/// </summary>
/// <remarks>
/// The batch bound is asserted as arithmetic over the transport's own ceiling rather than against a
/// number written here, so a later batch size that breaks the bound reddens. Nothing about it can be
/// observed against a small fixture, which is the reason it is pinned at all.
/// </remarks>
public sealed class SyncPreviewJobTests
{
    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>One batch's worst-case answer stays inside what the transport will read.</summary>
    /// <remarks>
    /// The transport refuses an answer past its bound outright rather than truncating it, so a batch
    /// whose every identifier is held would answer nothing at all and the count would fail on a
    /// library that is simply already synced.
    /// </remarks>
    [Fact]
    public void OneBatchsWorstCaseAnswerStaysInsideWhatTheTransportWillRead()
    {
        var worstCase = (long)SyncPreviewJob.ChunkSize * SyncPreviewJob.MeasuredBytesPerHit;

        Assert.True(
            worstCase < WhisparrClient.MaxResponseBytes,
            $"a full batch answered in its entirety is {worstCase} bytes, past the "
                + $"{WhisparrClient.MaxResponseBytes}-byte bound the transport reads within.");
    }

    /// <summary>The composed body is a bare array of identifier strings.</summary>
    /// <remarks>
    /// Parsed rather than compared as text. An object naming the identifiers as a member is answered
    /// 400 by a real instance and would satisfy any assertion made on the body's characters.
    /// </remarks>
    [Fact]
    public async Task TheComposedBodyIsABareArrayOfIdentifiers()
    {
        var bytes = BodyRecordingHandler.Answering(HttpStatusCode.OK, "[]");
        var client = TestWhisparrClient.Over(bytes);

        await client.ReduceHeldScenesAsync(Instance, Key, [FirstScene, SecondScene], TestCt);

        var sent = Assert.Single(bytes.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
        Assert.Equal(
            [FirstScene, SecondScene],
            body.RootElement.EnumerateArray().Select(id => id.GetString()));
    }

    /// <summary>
    /// An identifier the instance holds counts as already there, and one it does not counts as not
    /// yet there, over more identifiers than one batch carries.
    /// </summary>
    /// <remarks>
    /// Seeded past the batch size on purpose, so the flush of the final partial batch is exercised:
    /// a run that only asked about full batches would silently drop the remainder.
    /// </remarks>
    [Fact]
    public async Task EachIdentifierIsClassifiedAndThePartialFinalBatchIsAskedAboutToo()
    {
        var identifiers = Identifiers(SyncPreviewJob.ChunkSize + 3);
        var instance = new HeldScenes(identifiers.Take(10));

        var counted = await RunAsync(identifiers, instance);

        Assert.NotNull(counted);
        Assert.Equal(10, counted.AlreadyThere);
        Assert.Equal(identifiers.Count - 10, counted.NotYetThere);
        Assert.Equal(2, instance.Asked.Count);
        Assert.Equal(SyncPreviewJob.ChunkSize, instance.Asked[0].Count);
        Assert.Equal(3, instance.Asked[1].Count);
    }

    /// <summary>
    /// A batch the instance did not answer ends the run and holds no count at all.
    /// </summary>
    /// <remarks>
    /// The whole point of the count arriving in one piece. A partial count written to the slot would
    /// be answered to the page as a complete one, and the reader would act on a figure that is a
    /// fraction of the truth.
    /// </remarks>
    [Fact]
    public async Task ABatchTheInstanceDidNotAnswerEndsTheRunAndHoldsNoCount()
    {
        var identifiers = Identifiers(SyncPreviewJob.ChunkSize + 3);
        var cache = new SyncPreviewCache(TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(identifiers, new HeldScenes([], failOnCall: 2), cache));

        Assert.Null(cache.Held(WhisparrGeneration.V3));
    }

    /// <summary>
    /// A count over a library the instance now holds more of reports fewer scenes left to send.
    /// </summary>
    /// <remarks>
    /// The honesty the comparison exists for. A count derived from Cove's own rows would report the
    /// same figure after a run as before it, and the reader would have no way to tell a sync that
    /// worked from one that did nothing.
    /// </remarks>
    [Fact]
    public async Task ASecondCountAfterTheInstanceTookScenesReportsFewerLeftToSend()
    {
        var identifiers = Identifiers(5);
        var instance = new HeldScenes([]);

        var first = await RunAsync(identifiers, instance);
        instance.NowHolds(identifiers.Take(3));
        var second = await RunAsync(identifiers, instance);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(5, first.NotYetThere);
        Assert.Equal(2, second.NotYetThere);
        Assert.Equal(3, second.AlreadyThere);
    }

    /// <summary>The count's answer is what the slot then holds.</summary>
    [Fact]
    public async Task TheCountsThreeNumbersAreWhatTheSlotThenHolds()
    {
        var cache = new SyncPreviewCache(TimeProvider.System);
        var identifiers = Identifiers(4);

        var counted = await RunAsync(identifiers, new HeldScenes(identifiers.Take(1)), cache);

        Assert.Equal(counted, cache.Held(WhisparrGeneration.V3));
    }

    /// <summary>A count reaching no instance holds nothing and reports nothing.</summary>
    [Fact]
    public async Task ACountThatCouldNotBeAimedHoldsNothing()
    {
        var cache = new SyncPreviewCache(TimeProvider.System);
        await using var provider = Scopes(new StubLibraryScenes([], 0), cache);

        var counted = await SyncPreviewJob.RunAsync(
            provider.GetRequiredService<IServiceScopeFactory>(),
            (_, _) => Task.FromResult<SyncPreviewAiming?>(null),
            NullLogger.Instance,
            TestCt);

        Assert.Null(counted);
        Assert.Null(cache.Held(WhisparrGeneration.V3));
    }

    private static readonly Uri Instance = new("http://whisparr-v3:6969");

    private const string Key = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";

    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    private static List<string> Identifiers(int count)
        => [.. Enumerable.Range(1, count).Select(n => $"{n:x8}-0000-4000-8000-000000000000")];

    private static Task<SyncPreviewView?> RunAsync(
        IReadOnlyList<string> identifiers, HeldScenes instance, SyncPreviewCache? cache = null)
    {
        var provider = Scopes(
            new StubLibraryScenes(identifiers, unidentified: 7),
            cache ?? new SyncPreviewCache(TimeProvider.System));

        return SyncPreviewJob.RunAsync(
            provider.GetRequiredService<IServiceScopeFactory>(),
            (_, _) => Task.FromResult<SyncPreviewAiming?>(
                new SyncPreviewAiming(WhisparrGeneration.V3, SyncRegisters.Scenes, instance.AskAsync)),
            NullLogger.Instance,
            TestCt);
    }

    private static ServiceProvider Scopes(
        ILibrarySceneIdentityPort identities, SyncPreviewCache cache)
        => new ServiceCollection()
            .AddSingleton(identities)
            .AddSingleton(cache)
            .BuildServiceProvider();

    /// <summary>What an instance holds, recording every batch it was asked about.</summary>
    private sealed class HeldScenes(IEnumerable<string> held, int? failOnCall = null)
    {
        private readonly HashSet<string> _held = new(held, StringComparer.OrdinalIgnoreCase);

        public List<IReadOnlyCollection<string>> Asked { get; } = [];

        public void NowHolds(IEnumerable<string> more) => _held.UnionWith(more);

        public Task<IReadOnlySet<string>> AskAsync(
            IReadOnlyCollection<string> foreignIds, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Asked.Add([.. foreignIds]);
            return Asked.Count == failOnCall
                ? throw new HttpRequestException("nothing answered")
                : Task.FromResult<IReadOnlySet<string>>(
                    foreignIds.Where(_held.Contains).ToHashSet(StringComparer.Ordinal));
        }
    }

    private sealed class StubLibraryScenes(IReadOnlyList<string> identifiers, int unidentified)
        : ILibrarySceneIdentityPort
    {
        public async IAsyncEnumerable<string> SceneIdentities(
            WhisparrGeneration generation, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var identity in identifiers)
            {
                ct.ThrowIfCancellationRequested();
                yield return identity;
            }

            await Task.CompletedTask;
        }

        public Task<int> CountUnidentifiedAsync(WhisparrGeneration generation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(unidentified);
        }
    }
}
