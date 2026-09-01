using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace WhisparrSync.Options;

/// <summary>
/// Serialises every mutation of the one stored options blob, across the request scopes and the
/// background worker alike.
/// </summary>
/// <remarks>
/// Cove's store carries no row version and no concurrency check, so two writers that each load the
/// blob, change one member and save the whole thing lose one another's change with nothing
/// observable happening. Whisparr delivers per file and in bursts while the backstop walks, so two
/// writers meeting is the ordinary case here.
/// <para>
/// Held across the load, the fold and the save, because the loss happens between a load and the save
/// built on it. Registered as a singleton: a gate per scope would be a gate per request.
/// </para>
/// <para>
/// The fold is synchronous, which is what keeps an outbound request or a host call from being
/// awaited while the gate is held.
/// </para>
/// </remarks>
public sealed class OptionsWriteGate(ILogger? logger = null) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _log = logger ?? NullLogger.Instance;

    /// <summary>
    /// Applies <paramref name="fold"/> to the stored options and answers what is persisted after the
    /// call.
    /// </summary>
    /// <remarks>
    /// <paramref name="fold"/> receives the options as this call loaded them rather than as an
    /// earlier reader saw them, so a mutation decided either side of a network round trip lands on
    /// the blob as it stands.
    /// <para>
    /// Nothing is written when the fold answers a value equal to the one it was given.
    /// </para>
    /// <para>
    /// Nothing is written either when a blob is stored that the model could not bind: the value the
    /// fold ran on is then the load's defaults, and saving it would replace the stored connection,
    /// watermarks, callback host and upgrade behaviour with them. Such a call answers the loaded
    /// defaults and does not throw.
    /// </para>
    /// </remarks>
    /// <param name="options">The store to load from and save to.</param>
    /// <param name="fold">The stored options to the ones to persist.</param>
    /// <param name="ct">Cancels the wait, the load and the save.</param>
    /// <returns>The options as they now stand.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> or <paramref name="fold"/> is null.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<WhisparrSyncOptions> MutateAsync(
        OptionsStore options,
        Func<WhisparrSyncOptions, WhisparrSyncOptions> fold,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fold);

        // Outside the try, so a cancelled wait releases nothing it never took.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var load = await options.LoadBoundAsync(ct).ConfigureAwait(false);
            var stored = load.Options;
            var next = fold(stored);
            if (next == stored)
            {
                return stored;
            }

            if (!load.Bound)
            {
                // Checked after the equal-value short circuit, so a fold that would have written
                // nothing anyway is not reported as a refusal.
                WhisparrSyncLog.OptionsMutationRefusedOverUnreadableBlob(_log);
                return stored;
            }

            await options.SaveAsync(next, ct).ConfigureAwait(false);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
