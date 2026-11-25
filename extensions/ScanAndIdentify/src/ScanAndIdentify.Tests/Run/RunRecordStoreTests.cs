using ScanAndIdentify.Contracts;
using ScanAndIdentify.Run;

namespace ScanAndIdentify.Tests.Run;

public sealed class RunRecordStoreTests
{
    [Fact]
    public async Task Load_AnswersNothing_WhenNoRunKeyIsStored()
    {
        var store = new RunRecordStore(new FakeStore());

        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task Load_AnswersEveryStoredField_AfterASave()
    {
        var fake = new FakeStore();
        var store = new RunRecordStore(fake);
        var saved = new RunRecord
        {
            RunId = "7f1c",
            StartedAt = new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc),
            Options = new() { BackfillFingerprints = false },
            Stages =
            [
                new() { Stage = RunStage.Scan, State = RunStageState.Running, JobId = "job-1" },
                new() { Stage = RunStage.Fingerprints, State = RunStageState.Skipped },
                new() { Stage = RunStage.Identify, State = RunStageState.Pending },
                new() { Stage = RunStage.Images, State = RunStageState.Pending },
            ],
        };

        await store.SaveAsync(saved);
        var loaded = await store.LoadAsync();

        Assert.Equal(1, fake.SetCallCount);
        Assert.Equal([RunRecordStore.Key], (await fake.GetAllAsync()).Keys);
        // Member by member, because the record's own equality compares the stage list by reference.
        Assert.NotNull(loaded);
        Assert.Equal(saved.RunId, loaded.RunId);
        Assert.Equal(saved.StartedAt, loaded.StartedAt);
        Assert.Null(loaded.EndedAt);
        Assert.Null(loaded.EndedReason);
        Assert.Equal(saved.Options, loaded.Options);
        Assert.Equal(saved.Stages, loaded.Stages);
    }

    [Fact]
    public async Task Load_AnswersASettledStagesDetail_AfterASave()
    {
        var fake = new FakeStore();
        var store = new RunRecordStore(fake);
        var detail = new RunStageDetail(1, "Scanning clip.mp4", 5000, 5000, 4920, 80, 0, "done", null);
        await store.SaveAsync(new RunRecord
        {
            RunId = "7f1c",
            Stages =
            [
                new() { Stage = RunStage.Scan, State = RunStageState.Succeeded, Detail = detail },
                new() { Stage = RunStage.Fingerprints, State = RunStageState.Pending },
                new() { Stage = RunStage.Identify, State = RunStageState.Pending },
                new() { Stage = RunStage.Images, State = RunStageState.Pending },
            ],
        });

        var loaded = await store.LoadAsync();

        var stored = loaded!.Stages[0].Detail;
        Assert.NotNull(stored);
        Assert.Equal(detail.Progress, stored.Progress);
        Assert.Equal(detail.SubTask, stored.SubTask);
        Assert.Equal(detail.UnitsTotal, stored.UnitsTotal);
        Assert.Equal(detail.UnitsCompleted, stored.UnitsCompleted);
        Assert.Equal(detail.UnitsSucceeded, stored.UnitsSucceeded);
        Assert.Equal(detail.UnitsFailed, stored.UnitsFailed);
        Assert.Equal(detail.UnitsSkipped, stored.UnitsSkipped);
        Assert.Equal(detail.Summary, stored.Summary);
        Assert.Null(stored.Error);
        Assert.Null(loaded.Stages[1].Detail);
    }

    [Fact]
    public async Task Save_WritesTheStageNamesInTheirWireSpelling()
    {
        var fake = new FakeStore();
        await new RunRecordStore(fake).SaveAsync(new RunRecord
        {
            RunId = "7f1c",
            Stages = [new() { Stage = RunStage.Fingerprints, State = RunStageState.Indeterminate }],
        });

        var stored = await fake.GetAsync(RunRecordStore.Key);

        Assert.Contains("\"fingerprints\"", stored, StringComparison.Ordinal);
        Assert.Contains("\"indeterminate\"", stored, StringComparison.Ordinal);
    }

    [Theory]
    // A blob that is not JSON at all, one naming no run, one whose stage list binds to null, and one
    // whose stages are not the four this extension addresses positionally.
    [InlineData("not json")]
    [InlineData("""{"RunId":"","Stages":[]}""")]
    [InlineData("""{"RunId":"7f1c","Stages":null}""")]
    [InlineData("""{"RunId":"7f1c","Stages":[{"Stage":"identify"},{"Stage":"scan"},{"Stage":"images"},{"Stage":"fingerprints"}]}""")]
    [InlineData("""{"RunId":"7f1c","Stages":[{"Stage":"scan"},{"Stage":"fingerprints"}]}""")]
    public async Task Load_AnswersNothing_ForABlobTheRecordCannotHonor(string blob)
    {
        var fake = new FakeStore();
        await fake.SetAsync(RunRecordStore.Key, blob);

        Assert.Null(await new RunRecordStore(fake).LoadAsync());
    }
}
