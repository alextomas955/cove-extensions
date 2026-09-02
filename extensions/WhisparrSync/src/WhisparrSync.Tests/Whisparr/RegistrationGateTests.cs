using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

/// <summary>
/// That two registrations cannot be inside the find-then-write pair at the same time.
/// </summary>
/// <remarks>
/// This is the property the duplicate depended on. Measured against a real Whisparr v3: two
/// registrations issued without awaiting between them both found no entry under this product's name,
/// both created one, and the instance refused neither - leaving two webhooks delivering every import
/// event. The sequential control left the count unchanged, so serialising the pair is the whole fix.
/// <para>
/// Asserted on the interleaving rather than on a call count: two calls that both created is also
/// "two calls", so a count cannot tell the defect from the fix.
/// </para>
/// </remarks>
public sealed class RegistrationGateTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ASecondRegistrationDoesNotEnterWhileTheFirstIsInside()
    {
        using var gate = new RegistrationGate();
        using var firstIsInside = new SemaphoreSlim(0, 1);
        using var releaseFirst = new SemaphoreSlim(0, 1);
        var inside = 0;
        var everOverlapped = false;

        var first = gate.RunAsync(
            async _ =>
            {
                if (Interlocked.Increment(ref inside) > 1)
                {
                    everOverlapped = true;
                }

                firstIsInside.Release();
                await releaseFirst.WaitAsync(Budget, TestContext.Current.CancellationToken);
                Interlocked.Decrement(ref inside);
                return 1;
            },
            TestContext.Current.CancellationToken);

        // The second is issued only once the first is provably inside, so this is a real overlap
        // attempt rather than two calls that happened to be ordered.
        Assert.True(
            await firstIsInside.WaitAsync(Budget, TestContext.Current.CancellationToken),
            "the first call never reached its critical section");

        var second = gate.RunAsync(
            _ =>
            {
                if (Interlocked.Increment(ref inside) > 1)
                {
                    everOverlapped = true;
                }

                Interlocked.Decrement(ref inside);
                return Task.FromResult(2);
            },
            TestContext.Current.CancellationToken);

        // The discriminating assertion: while the first still holds the gate, the second must not
        // have run. Without the gate it would already have completed by now.
        Assert.False(second.IsCompleted, "the second call entered while the first was still inside");

        releaseFirst.Release();
        Assert.Equal(1, await first);
        Assert.Equal(2, await second);
        Assert.False(everOverlapped, "two calls were inside the gate at once");
    }

    [Fact]
    public async Task TheGateIsReleasedWhenTheCallItRanThrew()
    {
        using var gate = new RegistrationGate();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gate.RunAsync<int>(
                _ => throw new InvalidOperationException("the registration failed"),
                TestContext.Current.CancellationToken));

        // The control on the test above: a gate that leaked its slot on a throw would satisfy every
        // mutual-exclusion assertion there and deadlock the next registration for the process's life.
        var after = gate.RunAsync(_ => Task.FromResult(7), TestContext.Current.CancellationToken);
        Assert.Equal(7, await after.WaitAsync(Budget, TestContext.Current.CancellationToken));
    }
}
