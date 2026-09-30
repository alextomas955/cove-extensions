using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Connection;

/// <summary>What both callback routes need to state the address this extension answers on.</summary>
/// <remarks>
/// The lockdown port decides whether an address may be shown at all, and the clock stamps what a
/// read records. Neither route composes an address without all four.
/// </remarks>
internal sealed record CallbackAddressing(
    OptionsStore Options,
    ICallbackSecretPort Secrets,
    IHostLockdownPort Lockdown,
    TimeProvider Clock);

/// <summary>What the register route additionally needs to write the notification into an instance.</summary>
/// <remarks>
/// The gate serialises the options write against every other writer, and the registration gate
/// closes the window spanning the write and the read-back that follows it.
/// </remarks>
internal sealed record CallbackRegistering(
    OptionsWriteGate Gate,
    ICredentialPort Credentials,
    IWhisparrNotificationPort Notifications,
    RegistrationGate Registrations);
