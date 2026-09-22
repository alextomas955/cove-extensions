using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace WhisparrSync.Contracts;

/// <summary>Whether this product's callback is registered on the connected instance.</summary>
/// <remarks>
/// Three values, not a boolean. <see cref="NotCheckedYet"/> is never stored or rendered as
/// <see cref="NotRegistered"/>: "we have not looked" and "it is not there" send a user somewhere
/// different. Selecting the other generation resets to <see cref="NotCheckedYet"/> rather than
/// carrying the first instance's answer across.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum RegistrationStatus
{
    /// <summary>Nothing has asked this instance yet. The value a generation starts at.</summary>
    NotCheckedYet,

    /// <summary>A read of this instance's notifications found this product's registration.</summary>
    Registered,

    /// <summary>A read of this instance's notifications found no registration of this product's.</summary>
    NotRegistered,
}

/// <summary>Where an inbound callback carried the secret it presented.</summary>
/// <remarks>
/// The two positions have different confidentiality: an address is written to the access log of every
/// proxy and load balancer on the delivery path, and a header is not.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum CallbackSecretPosition
{
    /// <summary>In the address, where intermediaries log it.</summary>
    Address,

    /// <summary>Somewhere other than the address.</summary>
    OutOfBand,
}

/// <summary>The import callback, as the settings page reads it.</summary>
/// <remarks>
/// The two address forms differ on purpose. <c>CopyableAddress</c> carries the secret because a
/// pasted address has nowhere else to put it. <c>RegisteredAddress</c> does not, because the secret
/// travels out of band wherever the connected generation can carry it;
/// <c>SecretTravelsOutOfBand</c> false is a generation gap and means the registered address has to
/// carry the secret itself.
/// <para>
/// <c>LastEventSecretPosition</c> is null when no callback has arrived, which is the "registered, no
/// events received yet" tell. <c>MissingSetting</c> names the connection setting that was empty when
/// a registration could not be attempted, and is null on any other answer. <c>Refusal</c> is present
/// only after a registration whose read-back did not find the address that was sent, so it reports
/// what the notification now says rather than what the write answered.
/// </para>
/// <para>
/// <c>HostAuthenticationRequired</c> false means registering would sign the operator out of this
/// Cove, so the surface states that before the gesture rather than after it. Whisparr verifies a
/// webhook by posting to it, and a Cove with authentication off reads that post as an instance
/// reachable from outside its own machine and turns authentication on for good.
/// </para>
/// </remarks>
public sealed record CallbackView(
    WhisparrGeneration Generation,
    RegistrationStatus Status,
    string CopyableAddress,
    string RegisteredAddress,
    bool SecretTravelsOutOfBand,
    CallbackSecretPosition? LastEventSecretPosition,
    ConnectionSetting? MissingSetting,
    string? Refusal,
    bool HostAuthenticationRequired);

/// <summary>One request to register the callback in the connected instance.</summary>
/// <remarks>
/// The address is the whole edited address a user may have corrected, or null to keep the stored
/// host. Only the part up to where this extension's own route begins is honoured, and the secret
/// registered is always this product's own.
/// </remarks>
public sealed record RegisterCallbackRequest(string? CallbackAddress);
