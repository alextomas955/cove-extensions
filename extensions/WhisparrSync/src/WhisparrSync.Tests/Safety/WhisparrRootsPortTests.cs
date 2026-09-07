using System.Net;
using System.Reflection;
using System.Text.Json;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Safety;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Safety;

/// <summary>
/// The executable contract for the one root-folder read path: the cache and its refill gate, the fail-closed rule
/// on a read that could not answer, the projection's fidelity to Whisparr's own response, and the classification
/// each caller branches on. Transport call COUNTS carry as much of the contract as the returned values — the cache
/// and the two pre-read refusals are only observable through them.
/// </summary>
/// <remarks>
/// The port takes a credential-resolver delegate rather than an extension store, so this class needs no Cove type:
/// it stays cove-free and OFF the csproj bare-CI Compile-Remove group, and the properties below therefore hold on
/// the cove-absent leg too.
/// </remarks>
[Trait("Tier", "L0")]
public sealed class WhisparrRootsPortTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task TwoSequentialReadsInsideTheLifetime_IssueOneCall_AndReturnTheSameSet()
    {
        var (port, client, handler) = PortFor(Lifetime, [Roots(("/data/media", true))]);

        var first = await port.ReadAsync(client, default);
        var second = await port.ReadAsync(client, default);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(first.Paths, second.Paths);
        Assert.Equal("/data/media", Assert.Single(second.Paths));
    }

    [Fact]
    public async Task AZeroLifetime_IssuesACallPerRead()
    {
        var (port, client, handler) = PortFor(
            TimeSpan.Zero, [Roots(("/data/media", true)), Roots(("/data/media", true))]);

        await port.ReadAsync(client, default);
        await port.ReadAsync(client, default);

        Assert.Equal(2, handler.CallCount);
    }

    /// <summary>
    /// The refill gate: several reads starting together against a cold cache must collapse into ONE transport call.
    /// </summary>
    /// <remarks>
    /// THE ONLY FACT IN THIS CLASS THAT FAILS IF THE GATE OR ITS INNER FRESHNESS TEST IS DROPPED — both sequential
    /// cache facts above pass identically against a port with no gate at all. It is not a micro-optimisation: the
    /// webhook ingest guard consults this read per event, so a lost gate turns a burst of deliveries into a burst
    /// of calls at the acquisition side. It asserts ONE call rather than "fewer than the reader count" because
    /// without the inner freshness test the waiters merely serialise and then each still issues its own read.
    /// </remarks>
    [Fact]
    public async Task ConcurrentReadsAgainstAColdCache_IssueExactlyOneCall()
    {
        const int Readers = 4;
        var release = new TaskCompletionSource();
        var (port, client, handler) = PortFor(
            Lifetime,
            [
                () =>
                {
                    release.Task.Wait(TimeSpan.FromSeconds(30));
                    return Roots(("/data/media", true))();
                },
            ]);

        // Holding the response open is what makes the count decisive: while it is held nothing can be cached, so
        // every reader must either be waiting at the gate or have issued its own call.
        var start = new TaskCompletionSource();
        var readers = Enumerable.Range(0, Readers)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                return await port.ReadAsync(client, default);
            }))
            .ToArray();
        start.SetResult();

        try
        {
            await WaitForFirstCallAsync(handler);
        }
        finally
        {
            release.SetResult();
        }

        var results = await Task.WhenAll(readers);

        Assert.Equal(1, handler.CallCount);
        Assert.All(results, r => Assert.Equal("/data/media", Assert.Single(r.Paths)));
    }

    /// <summary>A read that could not answer must leave the cache untouched, so the next read retries.</summary>
    /// <remarks>
    /// The highest-risk property here, and a safety one rather than a performance one: the fail-closed ingest guard
    /// rejects every path while the root set is unknown, so a cached empty set would hold it closed for the whole
    /// lifetime — every webhook delivery in that window rejected even after Whisparr came back. The opposite
    /// shortcut ("roots unknown means allow") would open the guard entirely.
    /// </remarks>
    [Fact]
    public async Task AFailedReadIsNotCached_TheNextReadSucceeds()
    {
        var (port, client, handler) = PortFor(Lifetime, [Unavailable(), Roots(("/data/media", true))]);

        var failed = await port.ReadAsync(client, default);
        var recovered = await port.ReadAsync(client, default);

        Assert.Equal(FolderOverlapReason.ReadFailed, failed.Reason);
        Assert.Empty(failed.Roots);
        Assert.Null(recovered.Reason);
        Assert.Equal("/data/media", Assert.Single(recovered.Paths));
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task TheProjection_KeepsWhisparrsOrder_DropsBlankPaths_AndPreservesAccessible()
    {
        var (port, client, _) = PortFor(Lifetime, [Roots(("/mnt/b", false), ("", true), ("/mnt/a", true))]);

        var read = await port.ReadAsync(client, default);

        // Whisparr's own order, un-deduped and unsorted: the add path is first-match-wins over this list, so a
        // sort would silently change which root an add lands in.
        Assert.Equal(2, read.Roots.Count);
        Assert.Equal("/mnt/b", read.Roots[0].Path);
        Assert.False(read.Roots[0].Accessible);
        Assert.Equal("/mnt/a", read.Roots[1].Path);
        Assert.True(read.Roots[1].Accessible);
    }

    [Fact]
    public async Task ABlankStoredHost_IsNotConfigured_WithNoWireCall()
    {
        var (port, client, handler) = PortFor(Lifetime, [Roots(("/data/media", true))], baseUrl: "");

        var read = await port.ReadAsync(client, default);

        Assert.Equal(FolderOverlapReason.NotConfigured, read.Reason);
        Assert.Empty(read.Roots);
        Assert.Null(read.FailureState);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task AnUnmanageableStoredVersion_IsUnsupportedVersion_WithNoWireCall()
    {
        var (port, client, handler) = PortFor(Lifetime, [Roots(("/data/media", true))], version: "v9");

        var read = await port.ReadAsync(client, default);

        Assert.Equal(FolderOverlapReason.UnsupportedVersion, read.Reason);
        Assert.Empty(read.Roots);
        Assert.Null(read.FailureState);
        Assert.Equal(0, handler.CallCount);
    }

    // One readFailed reason, distinct transport states behind it: the add path propagates that state and the status
    // record reads the same classification, both from this one read. (Two facts rather than a theory because
    // WhisparrResultState is internal and cannot be a public test parameter.)
    [Fact]
    public Task ARejectedKey_IsReadFailed_KeepingItsBadKeyState()
        => AssertReadFailed(
            FakeHttpMessageHandler.Respond(HttpStatusCode.Unauthorized, "application/json", "{}"),
            WhisparrResultState.BadKey);

    [Fact]
    public Task AConnectionFailure_IsReadFailed_KeepingItsUnreachableState()
        => AssertReadFailed(
            FakeHttpMessageHandler.Throw(new HttpRequestException("connection refused")),
            WhisparrResultState.Unreachable);

    private static async Task AssertReadFailed(Func<HttpResponseMessage> response, WhisparrResultState expected)
    {
        var (port, client, _) = PortFor(Lifetime, [response]);

        var read = await port.ReadAsync(client, default);

        Assert.Equal(FolderOverlapReason.ReadFailed, read.Reason);
        Assert.Equal(expected, read.FailureState);
    }

    // Exhaustiveness over the wire vocabulary: a fifth reason fails here until it is either produced by a case
    // above or explicitly classified as one this port cannot reach. coveRootsUnknown is the only such reason today
    // — it is about Cove's own library roots, which this port never reads.
    [Fact]
    public async Task EveryReasonInTheVocabulary_IsEitherAPortOutcome_OrExplicitlyNotOne()
    {
        string[] producedHere =
        [
            FolderOverlapReason.NotConfigured,
            FolderOverlapReason.UnsupportedVersion,
            FolderOverlapReason.ReadFailed,
        ];
        string[] notReachableByThePort = [FolderOverlapReason.CoveRootsUnknown];

        var vocabulary = typeof(FolderOverlapReason)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

        Assert.Equal(
            producedHere.Concat(notReachableByThePort).Order(StringComparer.Ordinal),
            vocabulary.Order(StringComparer.Ordinal));

        // Each reason this class claims to produce is produced by a real read, not merely named in the list above.
        var observed = await ObservedReasonsAsync();
        Assert.Equal(producedHere.Order(StringComparer.Ordinal), observed.Order(StringComparer.Ordinal));
    }

    private static async Task<string[]> ObservedReasonsAsync()
    {
        var (blankHost, blankHostClient, _) = PortFor(Lifetime, [Roots(("/data/media", true))], baseUrl: "");
        var (badVersion, badVersionClient, _) = PortFor(Lifetime, [Roots(("/data/media", true))], version: "v9");
        var (failed, failedClient, _) = PortFor(Lifetime, [Unavailable()]);

        return
        [
            (await blankHost.ReadAsync(blankHostClient, default)).Reason!,
            (await badVersion.ReadAsync(badVersionClient, default)).Reason!,
            (await failed.ReadAsync(failedClient, default)).Reason!,
        ];
    }

    private static async Task WaitForFirstCallAsync(FakeHttpMessageHandler handler)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (handler.CallCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        Assert.True(handler.CallCount > 0, "no reader reached the transport — the refill gate never let one through.");
    }

    private static (WhisparrRootsPort Port, WhisparrClient Client, FakeHttpMessageHandler Handler) PortFor(
        TimeSpan lifetime,
        Func<HttpResponseMessage>[] responses,
        string baseUrl = BaseUrl,
        string version = "v3")
    {
        var handler = FakeHttpMessageHandler.Sequence(responses);
        var options = new WhisparrOptions { BaseUrl = baseUrl, ApiKey = ApiKey, SelectedVersion = version };
        var port = new WhisparrRootsPort(
            _ => Task.FromResult((options, options.BaseUrl, options.ApiKey)), lifetime);
        return (port, new WhisparrClient(new HttpClient(handler)), handler);
    }

    private static Func<HttpResponseMessage> Roots(params (string Path, bool Accessible)[] rows)
        => FakeHttpMessageHandler.Respond(
            HttpStatusCode.OK,
            "application/json",
            JsonSerializer.Serialize(
                Array.ConvertAll(
                    rows, r => new { id = 2, path = r.Path, accessible = r.Accessible, freeSpace = 1L })));

    private static Func<HttpResponseMessage> Unavailable()
        => FakeHttpMessageHandler.Respond(HttpStatusCode.ServiceUnavailable, "application/json", "{}");
}
