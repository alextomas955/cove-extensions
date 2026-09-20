namespace WhisparrSync.Whisparr;

/// <summary>Serialises the callback registration round trip against this instance.</summary>
/// <remarks>
/// Registering finds the notification and then creates or updates it. Two registrations overlapping
/// that pair both find none and both create one, and the instance accepts the second even when the
/// address matches, so every import event is then delivered twice.
/// <para>
/// Held as a singleton: the window spans two requests, so scoped state would serialise nothing. It
/// prevents a duplicate rather than repairing one, since no route here issues a delete.
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
