using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace WhisparrSync.Contracts;

/// <summary>Whether this product's callback is registered on the connected instance.</summary>
/// <remarks>
/// Three values, not a boolean. The third exists so that selecting the other generation never carries
/// the first instance's answer across, and it is never stored or rendered as
/// <see cref="NotRegistered"/> — "we have not looked" and "it is not there" send a user somewhere
/// different.
/// <para>
/// The wire spelling is declared on the type. An equivalent converter in a serializer options
/// collection would outrank this one rather than duplicate it.
/// </para>
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
/// proxy and load balancer on the delivery path, and a header is not. The wire spelling is declared on
/// the type.
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
/// Two address forms, deliberately different. The copyable one carries the secret because a pasted
/// address has nowhere else to put it; the registered one does not, because the secret travels out of
/// band wherever the connected generation can carry it.
/// </remarks>
/// <param name="Generation">The generation these values describe.</param>
/// <param name="Status">Whether the callback is registered, as of the last check.</param>
/// <param name="CopyableAddress">The address to hand a user, with the secret in it.</param>
/// <param name="RegisteredAddress">The address this product registers, with no secret in it.</param>
/// <param name="SecretTravelsOutOfBand">
/// Whether this generation can carry the secret off the address at all. False is a generation gap,
/// and means the registered address has to carry the secret itself.
/// </param>
/// <param name="LastEventSecretPosition">
/// Where the most recent inbound callback carried its secret, or null when none has arrived. Null is
/// the "registered, no events received yet" tell; <see cref="CallbackSecretPosition.Address"/> is what
/// the standing note about the less private form is shown for, and it clears itself when an event
/// arrives carrying the secret out of band.
/// </param>
/// <param name="MissingSetting">
/// Which connection setting was empty when a registration could not be attempted, or null on any
/// other answer. Named so the sentence points at the field that is actually empty rather than at the
/// pair.
/// </param>
/// <param name="Refusal">
/// What the instance refused, in its own words, or null when nothing was refused. Present only after
/// a registration whose READ-BACK did not find the address that was sent, so it reports what the
/// notification now says rather than what the write answered.
/// </param>
public sealed record CallbackView(
    WhisparrGeneration Generation,
    RegistrationStatus Status,
    string CopyableAddress,
    string RegisteredAddress,
    bool SecretTravelsOutOfBand,
    CallbackSecretPosition? LastEventSecretPosition,
    ConnectionSetting? MissingSetting,
    string? Refusal);

/// <summary>One request to register the callback in the connected instance.</summary>
/// <remarks>
/// The address is the whole edited address a user may have corrected. Only the part up to where this
/// extension's own route begins is honoured, and the secret registered is always this product's own.
/// </remarks>
/// <param name="CallbackAddress">
/// The edited callback address, or null to keep whatever host is stored.
/// </param>
public sealed record RegisterCallbackRequest(string? CallbackAddress);

/// <summary>What the inbound callback route answers a delivery it accepted with.</summary>
/// <remarks>
/// Reports where the delivery carried its secret, which is the one thing about the request this phase
/// establishes and the reading the note about the less private form is shown for. What arrives in the
/// body of a delivery is Phase 52's and nothing here reads it.
/// </remarks>
/// <param name="SecretPosition">Where this delivery carried its secret.</param>
public sealed record CallbackAcknowledgement(CallbackSecretPosition SecretPosition);
