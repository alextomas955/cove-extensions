using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

// The registration reading a stored connection test takes. Registering is not this double's
// business: nothing that reaches it registers anything.
internal sealed class ReadingNotificationPort : IWhisparrNotificationPort
{
    private readonly RegistrationStatus status;
    private readonly Exception? failure;

    private ReadingNotificationPort(RegistrationStatus status, Exception? failure)
    {
        this.status = status;
        this.failure = failure;
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

        return failure is not null
            ? Task.FromException<CallbackRegistrationOutcome>(failure)
            : Task.FromResult(new CallbackRegistrationOutcome(status, null, false, null));
    }
}
