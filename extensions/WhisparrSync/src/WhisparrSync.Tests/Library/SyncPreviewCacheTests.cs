using WhisparrSync.Contracts;
using WhisparrSync.Library;

namespace WhisparrSync.Tests.Library;

// The expiry is driven by a clock the test moves rather than by a sleep, so the boundary itself is
// the subject.
public sealed class SyncPreviewCacheTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ACountJustTakenIsAnsweredWithItsThreeNumbers()
    {
        var clock = new SteppingClock(Start);
        var cache = new SyncPreviewCache(clock);

        cache.Hold(WhisparrGeneration.V3, Counted(5898, 4, 1648));

        var held = cache.Held(WhisparrGeneration.V3);
        Assert.NotNull(held);
        Assert.Equal(5898, held.NotYetThere);
        Assert.Equal(4, held.AlreadyThere);
        Assert.Equal(1648, held.Skipped);
    }

    [Fact]
    public void ACountInsideItsLifetimeIsStillAnswered()
    {
        var clock = new SteppingClock(Start);
        var cache = new SyncPreviewCache(clock);
        cache.Hold(WhisparrGeneration.V3, Counted(1, 2, 3));

        clock.Advance(SyncPreviewCache.Lifetime - TimeSpan.FromSeconds(1));

        Assert.NotNull(cache.Held(WhisparrGeneration.V3));
    }

    // Nothing, rather than the figures with a stale flag beside them. A caller handed both would
    // have to decide what to do with a stale count.
    [Fact]
    public void ACountPastItsLifetimeIsAnsweredByNothing()
    {
        var clock = new SteppingClock(Start);
        var cache = new SyncPreviewCache(clock);
        cache.Hold(WhisparrGeneration.V3, Counted(1, 2, 3));

        clock.Advance(SyncPreviewCache.Lifetime);

        Assert.Null(cache.Held(WhisparrGeneration.V3));
    }

    // The two generations compare against different namespaces and register different things, so a
    // count taken against one is a wrong answer for the other, not an approximate one.
    [Fact]
    public void ACountIsAnsweredOnlyForTheGenerationItWasTakenAgainst()
    {
        var cache = new SyncPreviewCache(new SteppingClock(Start));

        cache.Hold(WhisparrGeneration.V3, Counted(1, 2, 3));

        Assert.NotNull(cache.Held(WhisparrGeneration.V3));
        Assert.Null(cache.Held(WhisparrGeneration.V2));
    }

    private static SyncPreviewView Counted(int notYetThere, int alreadyThere, int skipped)
        => new(notYetThere, alreadyThere, skipped, SyncRegisters.Scenes, Start);

    private sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
