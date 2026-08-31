using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace WhisparrSync.Contracts;

/// <summary>What the inbound callback did with one delivery, as the delivery is told.</summary>
/// <remarks>
/// Two values, and deliberately coarse. Whether a file was found, which candidate verified, and
/// whether anything reached the library are all withheld: the caller is anonymous, and an answer
/// that varied with what is on disk would turn this route into a filesystem probe.
/// <para>
/// The wire spelling is declared on the type. An equivalent converter in a serializer options
/// collection would outrank this one rather than duplicate it.
/// </para>
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum ImportEventOutcome
{
    /// <summary>
    /// The delivery named an event this product acts on, and it was acted on. It does not say a file
    /// was imported.
    /// </summary>
    Accepted,

    /// <summary>The delivery named an event this product does not act on, and nothing was done.</summary>
    Ignored,
}

/// <summary>What the inbound callback answers a delivery it authenticated with.</summary>
/// <remarks>
/// Reports where the delivery carried its secret, which is the reading the note about the less
/// private form is shown for, and how the event was classified. Neither member varies with the
/// contents of the filesystem.
/// </remarks>
/// <param name="SecretPosition">Where this delivery carried its secret.</param>
/// <param name="Outcome">Whether this product acts on the event the delivery named.</param>
public sealed record ImportAcknowledgement(
    CallbackSecretPosition SecretPosition,
    ImportEventOutcome Outcome);
