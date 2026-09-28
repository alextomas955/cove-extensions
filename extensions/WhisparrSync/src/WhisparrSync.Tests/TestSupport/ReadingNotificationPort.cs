using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

// The registration reading a stored connection test takes. Registering is not this double's
// business: nothing that reaches it registers anything.
internal sealed class ReadingNotificationPort : IWhisparrNotificationPort
{
    private readonly RegistrationStatus _status;
    private readonly Exception? _failure;

    private ReadingNotificationPort(RegistrationStatus status, Exception? failure)
    {
        _status = status;
        _failure = failure;
    }

    internal List<WhisparrGeneration> Reads { get; } = [];

    internal static ReadingNotificationPort Answering(RegistrationStatus status)
        => new(status, null);

    // The one failure a read may raise: no whole answer arrived.
    internal static ReadingNotificationPort Unreachable()
        => new(RegistrationStatus.NotCheckedYet, new HttpRequestException("no answer"));

    public Task<CallbackRegistrationOutcome> RegisterAsync(
        WhisparrBinding binding, string callbackAddress, string secret, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<CallbackRegistrationOutcome> ReadAsync(WhisparrBinding binding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);
        Reads.Add(binding.Generation);

        return _failure is not null
            ? Task.FromException<CallbackRegistrationOutcome>(_failure)
            : Task.FromResult(new CallbackRegistrationOutcome(_status, null, false, null));
    }
}
