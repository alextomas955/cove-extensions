namespace WhisparrSync.Whisparr;

/// <summary>Serialises the callback registration round trip against this instance.</summary>
/// <remarks>
/// Registering finds this product's notification and then creates or updates it. Two registrations
/// overlapping that pair both find none and both create one, and the instance does not refuse the
/// second when the address matches — so the duplicate stays, delivering every import event twice.
/// <para>
/// Held as a singleton, because the window to close spans two requests rather than one. Scoped state
/// would serialise nothing.
/// </para>
/// <para>
/// This prevents a duplicate rather than repairing one. Removing an entry already present would need
/// an outbound delete, a verb no route of this product declares.
/// </para>
/// </remarks>
public sealed class RegistrationGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Runs <paramref name="register"/> with no other registration in flight.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled while waiting.</exception>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> register, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(register);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await register(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
