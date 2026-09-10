using WhisparrSync.Contracts;
using WhisparrSync.Library;

namespace WhisparrSync.Tests.Library;

/// <summary>
/// How long a count is answered for, and whose count it is.
/// </summary>
/// <remarks>
/// The expiry is driven by a clock the test moves rather than by a sleep, so the boundary itself is
/// the subject. A count older than its lifetime cannot be answered at all, which is what stops a
/// stale figure reaching the page without its age.
/// </remarks>
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

    /// <summary>A count past its lifetime is answered by nothing at all.</summary>
    /// <remarks>
    /// Nothing rather than the figures with a flag beside them: a caller handed both would have to
    /// decide what to do with a stale count, and the answer this product gives is that there is
    /// none.
    /// </remarks>
    [Fact]
    public void ACountPastItsLifetimeIsAnsweredByNothing()
    {
        var clock = new SteppingClock(Start);
        var cache = new SyncPreviewCache(clock);
        cache.Hold(WhisparrGeneration.V3, Counted(1, 2, 3));

        clock.Advance(SyncPreviewCache.Lifetime);

        Assert.Null(cache.Held(WhisparrGeneration.V3));
    }

    /// <summary>One generation's count is not answered for the other.</summary>
    /// <remarks>
    /// The two compare against different namespaces and register different things, so a count taken
    /// against one would be a wrong answer for the other rather than an approximate one.
    /// </remarks>
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
