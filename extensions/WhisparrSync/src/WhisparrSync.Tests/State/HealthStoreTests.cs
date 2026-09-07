using WhisparrSync.Client;
using WhisparrSync.Ingest;
using WhisparrSync.State;

namespace WhisparrSync.Tests.State;

/// <summary>
/// The write-half contract for <see cref="HealthStore"/>: the record is STICKY — a success advances the healthy
/// tick and resets the consecutive count but never clears the last error or the last-failure tick, which is the
/// one behaviour inverted from <see cref="ImportLog"/>. A dependency never observed is absent from the
/// projection rather than projected healthy. Stickiness is bounded for the error TEXT only, by
/// <see cref="DependencyHealth.RecoveredErrorRetentionTicks"/>, and only once the dependency has recovered.
/// </summary>
/// <remarks>
/// Every fact that asserts a retained error reads through the explicit-clock <c>LoadAsync</c> overload. The
/// synthetic write ticks are single digits to hundreds, so an ambient-clock read would sit two millennia past the
/// horizon and would test expiry where the fact means to test retention.
/// </remarks>
[Trait("Tier", "L0")]
public sealed class HealthStoreTests
{
    [Fact]
    public async Task Failure_RecordsTheOutcome_TheFailureTick_AndTheReasonText()
    {
        var health = new HealthStore(new FakeStore());

        await health.RecordAsync(
            HealthDependency.Acquisition,
            HealthOutcome.FromAcquisition(WhisparrResultState.Unreachable, "Network is unreachable (host:6999)"),
            100L);

        var entry = Assert.Single(await health.LoadAsync());
        Assert.Equal(HealthDependency.Acquisition, entry.Dependency);
        Assert.Equal("unreachable", entry.Outcome);
        Assert.Equal(100L, entry.LastFailureTicks);
        Assert.Equal(1, entry.ConsecutiveFailures);
        Assert.Equal("Network is unreachable (host:6999)", entry.LastError);
    }

    [Fact]
    public async Task Success_AfterAFailure_KeepsTheErrorAndTheFailureTick()
    {
        var health = new HealthStore(new FakeStore());
        await health.RecordAsync(
            HealthDependency.Acquisition,
            HealthOutcome.FromAcquisition(WhisparrResultState.Unreachable, "Network is unreachable (host:6999)"),
            100L);

        await health.RecordAsync(
            HealthDependency.Acquisition, HealthOutcome.FromAcquisition(WhisparrResultState.Ok, null), 200L);

        var entry = Assert.Single(await health.LoadAsync(200L));
        Assert.Equal("ok", entry.Outcome);
        Assert.Equal(0, entry.ConsecutiveFailures);
        Assert.Equal(200L, entry.LastHealthyTicks);
        Assert.Equal(100L, entry.LastFailureTicks);
        Assert.Equal("Network is unreachable (host:6999)", entry.LastError);
    }

    [Fact]
    public async Task RecoveredDependency_ProjectsBothTicksAndTheRetainedError()
    {
        var health = new HealthStore(new FakeStore());
        await health.RecordAsync(
            HealthDependency.Acquisition,
            HealthOutcome.FromAcquisition(WhisparrResultState.Unreachable, "Network is unreachable (host:6999)"),
            100L);
        await health.RecordAsync(
            HealthDependency.Acquisition, HealthOutcome.FromAcquisition(WhisparrResultState.Ok, null), 200L);

        var view = Assert.Single(WhisparrSync.PipelineHealthOf(await health.LoadAsync(200L)));
        Assert.Equal(200L, view.LastHealthyTicks);
        Assert.Equal(100L, view.LastFailureTicks);
        Assert.NotEmpty(view.LastError);
    }

    [Fact]
    public async Task NeverObservedDependency_IsAbsentFromTheProjection_NotReportedHealthy()
    {
        var health = new HealthStore(new FakeStore());
        await health.RecordAsync(
            HealthDependency.Acquisition, HealthOutcome.FromAcquisition(WhisparrResultState.Ok, null), 100L);

        var views = WhisparrSync.PipelineHealthOf(await health.LoadAsync());

        Assert.Equal([HealthDependency.Acquisition], views.Select(v => v.Dependency));
    }

    [Fact]
    public async Task HandEditedBlob_WithJunkAndDuplicateEntries_LoadsAtMostTheClosedKeySet()
    {
        var rows = new List<string>();
        for (var i = 0; i < 50; i++)
        {
            var dependency = (i % 5) switch
            {
                0 => HealthDependency.Acquisition,
                1 => HealthDependency.Metadata,
                2 => HealthDependency.Import,
                3 => $"junk-{i}",
                _ => "",
            };
            rows.Add(Row(dependency, i));
        }

        var store = new FakeStore();
        await store.SetAsync(HealthKey, $"[{string.Join(",", rows)}]");

        var loaded = await new HealthStore(store).LoadAsync();

        Assert.True(loaded.Length <= DependencyHealth.MaxEntries, $"loaded {loaded.Length} entries");
        Assert.All(loaded, e => Assert.Contains(e.Dependency, HealthDependency.All));
        Assert.Equal(loaded.Length, loaded.Select(e => e.Dependency).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task RejectionWithAHugeProviderMessage_TruncatesToTheErrorLengthCap()
    {
        var store = new FakeStore();
        var health = new HealthStore(store);

        await health.RecordAsync(
            HealthDependency.Acquisition,
            HealthOutcome.FromAcquisition(WhisparrResultState.Rejected, new string('x', 100_000)),
            100L);

        var entry = Assert.Single(await health.LoadAsync());
        Assert.Equal(DependencyHealth.MaxErrorLength, entry.LastError.Length);
    }

    [Fact]
    public async Task TenThousandRecords_LeaveTheBlobUnderACeilingDerivedFromTheConsts()
    {
        var store = new FakeStore();
        var health = new HealthStore(store);

        for (var i = 1; i <= 10_000; i++)
        {
            var dependency = HealthDependency.All[i % HealthDependency.All.Length];
            var observation = i % 2 == 0
                ? HealthOutcome.FromAcquisition(WhisparrResultState.Ok, null)
                : HealthOutcome.FromAcquisition(WhisparrResultState.Rejected, new string('y', 4_000));
            await health.RecordAsync(dependency, observation, i);
        }

        // Six JSON scalars per entry with their property names, quoting and punctuation; 256 characters is a
        // generous upper bound on that fixed overhead. The ceiling is DERIVED from the two consts because the
        // requirement is that the bound be proven — a hard-coded byte count proves nothing.
        const int PerEntryScalarOverhead = 256;
        var ceiling = 2 + (DependencyHealth.MaxEntries * (PerEntryScalarOverhead + DependencyHealth.MaxErrorLength));

        var blob = await store.GetAsync(HealthKey);
        Assert.NotNull(blob);
        Assert.True(blob!.Length < ceiling, $"blob was {blob.Length} bytes, ceiling {ceiling}");
    }

    [Fact]
    public async Task FailureFailureSuccess_ResetsOnlyTheCount_AndAdvancesOnlyTheHealthyTick()
    {
        var health = new HealthStore(new FakeStore());
        await health.RecordAsync(
            HealthDependency.Metadata, HealthObservation.Failure("unreachable", "first"), 100L);
        await health.RecordAsync(
            HealthDependency.Metadata, HealthObservation.Failure("unreachable", "second"), 200L);

        await health.RecordAsync(HealthDependency.Metadata, HealthObservation.Healthy("ok"), 300L);

        var entry = Assert.Single(await health.LoadAsync(300L));
        Assert.Equal(0, entry.ConsecutiveFailures);
        Assert.Equal(300L, entry.LastHealthyTicks);
        Assert.Equal(200L, entry.LastFailureTicks);
        Assert.Equal("second", entry.LastError);
    }

    [Fact]
    public Task BadKey_CarriesNoReason_AndStillRecordsASynthesizedErrorSentence()
        => AssertSynthesizedError(WhisparrResultState.BadKey);

    [Fact]
    public Task NotWhisparr_CarriesNoReason_AndStillRecordsASynthesizedErrorSentence()
        => AssertSynthesizedError(WhisparrResultState.NotWhisparr);

    // The state parameter keeps the two facts one assertion: WhisparrResultState is internal, so it cannot
    // appear on a public [Theory] signature.
    private static async Task AssertSynthesizedError(WhisparrResultState state)
    {
        var health = new HealthStore(new FakeStore());

        await health.RecordAsync(
            HealthDependency.Acquisition, HealthOutcome.FromAcquisition(state, null), 100L);

        var entry = Assert.Single(await health.LoadAsync());
        Assert.NotEmpty(entry.LastError);
        Assert.Equal(1, entry.ConsecutiveFailures);
    }

    [Theory]
    [InlineData("}{ not json")]
    [InlineData("{\"Dependency\":\"acquisition\",\"Outcome\":\"ok\"}")]
    public async Task HostileBlob_LoadsEmpty_NeverThrows_AndTheNextRecordStillPersists(string blob)
    {
        var store = new FakeStore();
        await store.SetAsync(HealthKey, blob);
        var health = new HealthStore(store);

        Assert.Empty(await health.LoadAsync());

        await health.RecordAsync(
            HealthDependency.Import, HealthObservation.Healthy("imported"), 400L);
        var entry = Assert.Single(await health.LoadAsync());
        Assert.Equal(400L, entry.LastHealthyTicks);
    }

    [Fact]
    public async Task ParallelRecords_DoNotTear_AndKeepAConsistentPerDependencyCount()
    {
        var store = new ConcurrentFakeStore();
        var health = new HealthStore(store);
        const int PerDependency = 40;

        await Task.WhenAll(HealthDependency.All.SelectMany(dependency =>
            Enumerable.Range(1, PerDependency).Select(i =>
                health.RecordAsync(dependency, HealthObservation.Failure("unreachable", $"{dependency}-{i}"), i))));

        var loaded = await health.LoadAsync();
        Assert.Equal(HealthDependency.All.Length, loaded.Length);
        Assert.All(loaded, e => Assert.Equal(PerDependency, e.ConsecutiveFailures));
    }

    [Fact]
    public async Task AlreadyCancelledToken_WritesNothing()
    {
        var store = new FakeStore();
        var health = new HealthStore(store);
        await health.RecordAsync(HealthDependency.Acquisition, HealthObservation.Healthy("ok"), 100L);
        var before = await store.GetAsync(HealthKey);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await health.RecordAsync(
            HealthDependency.Acquisition, HealthObservation.Failure("unreachable", "shutdown"), 200L,
            cancelled.Token);

        Assert.Equal(before, await store.GetAsync(HealthKey));
    }

    [Fact]
    public Task NotAddedAnswer_LeavesTheDependencyHealthy() => AssertDataOutcomeIsHealthy(WhisparrResultState.Absent);

    [Fact]
    public Task AlreadyExistsAnswer_LeavesTheDependencyHealthy()
        => AssertDataOutcomeIsHealthy(WhisparrResultState.Conflict);

    private static async Task AssertDataOutcomeIsHealthy(WhisparrResultState state)
    {
        var health = new HealthStore(new FakeStore());

        await health.RecordAsync(
            HealthDependency.Acquisition, HealthOutcome.FromAcquisition(state, null), 100L);

        var entry = Assert.Single(await health.LoadAsync());
        Assert.Equal(0, entry.ConsecutiveFailures);
        Assert.Equal(100L, entry.LastHealthyTicks);
        Assert.Empty(entry.LastError);
    }

    /// <summary>
    /// The unit-tested bound for a state no browser evidence can reach on this fixture: only v2 and v3 instances
    /// exist, and a wrong key short-circuits to a bad-key classification before any version parse — so
    /// <c>versionMismatch</c> is not inducible live and is proven here instead.
    /// </summary>
    [Fact]
    public async Task VersionMismatch_ClassifiesAsAFailureWithItsOwnOutcomeString()
    {
        var health = new HealthStore(new FakeStore());

        await health.RecordAsync(
            HealthDependency.Acquisition,
            HealthOutcome.FromAcquisition(WhisparrResultState.VersionMismatch, "1.0.0.1"),
            100L);

        var entry = Assert.Single(await health.LoadAsync());
        Assert.Equal("versionMismatch", entry.Outcome);
        Assert.Equal(1, entry.ConsecutiveFailures);
        Assert.NotEmpty(entry.LastError);
    }

    [Fact]
    public async Task PersistedBlob_NeverContainsTheApiKeyOrTheWebhookSecret()
    {
        const string StoredApiKey = "SENTINEL-API-KEY-0f1e2d3c";
        const string WebhookSecret = "SENTINEL-WEBHOOK-SECRET-9a8b7c6d";
        var store = new FakeStore();
        var health = new HealthStore(store);

        // Whisparr's own message is arbitrary text and may itself look key-shaped; what this pins is that the
        // record is never the place the STORED values leak, because neither is ever passed to it.
        await health.RecordAsync(
            HealthDependency.Acquisition,
            HealthOutcome.FromAcquisition(WhisparrResultState.Rejected, "rejected by 4f3e2d1c0b9a8f7e6d5c4b3a"),
            100L);

        var blob = await store.GetAsync(HealthKey);
        Assert.NotNull(blob);
        Assert.DoesNotContain(StoredApiKey, blob, StringComparison.Ordinal);
        Assert.DoesNotContain(WebhookSecret, blob, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetadataWithNoProviderCredential_IsNotChecked_NotHealthyAndNotFailed()
    {
        var health = new HealthStore(new FakeStore());

        await health.RecordAsync(
            HealthDependency.Metadata,
            HealthOutcome.FromMetadata(providerCredentialResolved: false, WhisparrResultState.Ok, null),
            100L);

        var entry = Assert.Single(await health.LoadAsync());
        Assert.Equal("needsProviderKey", entry.Outcome);
        Assert.Equal(0, entry.ConsecutiveFailures);
        Assert.Equal(0L, entry.LastHealthyTicks);
        Assert.Equal(0L, entry.LastFailureTicks);
    }

    [Fact]
    public async Task PathOutsideAKnownRoot_DoesNotCountAsAFailure_ButPathNotVisibleDoes()
    {
        var health = new HealthStore(new FakeStore());

        await health.RecordAsync(
            HealthDependency.Import,
            HealthOutcome.FromImport("Flagged", "path outside known Whisparr root"),
            100L);
        Assert.Equal(0, (await health.LoadAsync())[0].ConsecutiveFailures);

        await health.RecordAsync(
            HealthDependency.Import,
            HealthOutcome.FromImport("Flagged", IngestCoordinator.PathNotVisibleReason),
            200L);

        var entry = Assert.Single(await health.LoadAsync());
        Assert.Equal("pathNotVisible", entry.Outcome);
        Assert.Equal(1, entry.ConsecutiveFailures);
        Assert.Equal(200L, entry.LastFailureTicks);
    }

    [Fact]
    public async Task DuplicateDelivery_LeavesTheFailureCountAndBothTicksUnchanged()
    {
        var health = new HealthStore(new FakeStore());
        await health.RecordAsync(HealthDependency.Import, HealthOutcome.FromImport("Imported", null), 100L);
        await health.RecordAsync(
            HealthDependency.Import,
            HealthOutcome.FromImport("Flagged", IngestCoordinator.PathNotVisibleReason),
            200L);

        await health.RecordAsync(
            HealthDependency.Import, HealthOutcome.FromImport("Skipped", "duplicate delivery"), 300L);

        var entry = Assert.Single(await health.LoadAsync());
        Assert.Equal("skipped", entry.Outcome);
        Assert.Equal(1, entry.ConsecutiveFailures);
        Assert.Equal(100L, entry.LastHealthyTicks);
        Assert.Equal(200L, entry.LastFailureTicks);
    }

    /// <summary>
    /// One failure, nothing re-probed it, and twenty hours have passed: the dependency is still broken, so it keeps
    /// its error, its count and its failure tick and still alarms. Age expires the text of a RECOVERY, never of a
    /// live fault — this is the one way the horizon could silence a real problem.
    /// </summary>
    [Fact]
    public async Task UnrecoveredFailure_TwentyHoursOld_KeepsItsErrorItsCountAndItsTick()
    {
        var failedAt = DependencyHealth.RecoveredErrorRetentionTicks;
        var health = new HealthStore(new FakeStore());
        await health.RecordAsync(
            HealthDependency.Acquisition,
            HealthOutcome.FromAcquisition(WhisparrResultState.Unreachable, "Network is unreachable (host:6999)"),
            failedAt);

        var entry = Assert.Single(await health.LoadAsync(failedAt + (20 * TimeSpan.TicksPerHour)));

        Assert.Equal("Network is unreachable (host:6999)", entry.LastError);
        Assert.Equal(1, entry.ConsecutiveFailures);
        Assert.Equal(failedAt, entry.LastFailureTicks);
    }

    [Fact]
    public async Task RecoveredDependency_ReadOneMinutePastTheWindow_LosesOnlyTheErrorText()
    {
        var failedAt = DependencyHealth.RecoveredErrorRetentionTicks;
        var health = await Recovered(failedAt);

        var entry = Assert.Single(await health.LoadAsync(
            failedAt + DependencyHealth.RecoveredErrorRetentionTicks + TimeSpan.TicksPerMinute));

        Assert.Empty(entry.LastError);
        Assert.Equal(failedAt, entry.LastFailureTicks);
        Assert.Equal(failedAt + TimeSpan.TicksPerMinute, entry.LastHealthyTicks);
        Assert.Equal("ok", entry.Outcome);
    }

    [Fact]
    public async Task RecoveredDependency_ReadAtExactlyTheWindow_StillCarriesItsError()
    {
        var failedAt = DependencyHealth.RecoveredErrorRetentionTicks;
        var health = await Recovered(failedAt);

        var entry = Assert.Single(
            await health.LoadAsync(failedAt + DependencyHealth.RecoveredErrorRetentionTicks));

        Assert.Equal("Network is unreachable (host:6999)", entry.LastError);
    }

    [Fact]
    public async Task AgedOutDependency_ThatFailsAgain_IsFullyRepopulatedAndAlarms()
    {
        var failedAt = DependencyHealth.RecoveredErrorRetentionTicks;
        var health = await Recovered(failedAt);
        var refailedAt = failedAt + DependencyHealth.RecoveredErrorRetentionTicks + TimeSpan.TicksPerMinute;

        await health.RecordAsync(
            HealthDependency.Acquisition,
            HealthOutcome.FromAcquisition(WhisparrResultState.Rejected, "Whisparr said no"),
            refailedAt);

        var entry = Assert.Single(await health.LoadAsync(refailedAt));
        Assert.Equal("Whisparr said no", entry.LastError);
        Assert.Equal(1, entry.ConsecutiveFailures);
        Assert.Equal(refailedAt, entry.LastFailureTicks);
    }

    [Fact]
    public async Task HandEditedBlob_PinningAnErrorAtAZeroFailureTick_ReadsAsInfinitelyOldAndExpires()
    {
        var store = new FakeStore();
        await store.SetAsync(
            HealthKey,
            """
            [{"Dependency":"acquisition","Outcome":"ok","LastHealthyTicks":1,"LastFailureTicks":0,"ConsecutiveFailures":0,"LastError":"pinned forever"}]
            """);

        var entry = Assert.Single(await new HealthStore(store).LoadAsync(DependencyHealth.RecoveredErrorRetentionTicks + 1));

        Assert.Empty(entry.LastError);
    }

    [Fact]
    public async Task FutureDatedFailureTick_IsNotEvidenceOfAge_AndKeepsItsError()
    {
        var health = await Recovered(10 * DependencyHealth.RecoveredErrorRetentionTicks);

        var entry = Assert.Single(await health.LoadAsync(DependencyHealth.RecoveredErrorRetentionTicks));

        Assert.Equal("Network is unreachable (host:6999)", entry.LastError);
    }

    [Fact]
    public async Task LoadingAnExpiredRecord_LeavesTheStoredBlobByteIdentical()
    {
        var store = new FakeStore();
        var health = await Recovered(DependencyHealth.RecoveredErrorRetentionTicks, store);
        var before = await store.GetAsync(HealthKey);
        var writes = store.SetCallCount;
        Assert.Contains("Network is unreachable", before ?? "", StringComparison.Ordinal);

        var loaded = Assert.Single(await health.LoadAsync(100 * DependencyHealth.RecoveredErrorRetentionTicks));

        Assert.Empty(loaded.LastError);
        Assert.Equal(before, await store.GetAsync(HealthKey), StringComparer.Ordinal);
        Assert.Equal(writes, store.SetCallCount);
    }

    // A failure at failedAt, then a success a minute later: the recovered shape every ageing fact starts from.
    private static async Task<HealthStore> Recovered(long failedAt, FakeStore? store = null)
    {
        var health = new HealthStore(store ?? new FakeStore());
        await health.RecordAsync(
            HealthDependency.Acquisition,
            HealthOutcome.FromAcquisition(WhisparrResultState.Unreachable, "Network is unreachable (host:6999)"),
            failedAt);
        await health.RecordAsync(
            HealthDependency.Acquisition,
            HealthOutcome.FromAcquisition(WhisparrResultState.Ok, null),
            failedAt + TimeSpan.TicksPerMinute);
        return health;
    }

    private const string HealthKey = "health";

    private static string Row(string dependency, int i)
        => $$"""
             {"Dependency":"{{dependency}}","Outcome":"ok","LastHealthyTicks":{{i}},"LastFailureTicks":0,"ConsecutiveFailures":0,"LastError":""}
             """;
}
