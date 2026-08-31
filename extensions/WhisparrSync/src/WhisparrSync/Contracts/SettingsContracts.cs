using System.Text.Json.Serialization;
using Cove.Extensions.Shared;
using WhisparrSync.Options;

namespace WhisparrSync.Contracts;

/// <summary>One generation's stored connection, as the settings page reads it.</summary>
/// <remarks>
/// The recorded version and the instant it was verified are separate nullable members from the instant
/// the instance last answered anything, because the two readings measure different things: a version is
/// as old as the test that read it, while reachability is as recent as the last answer of any kind.
/// <para>
/// A null <paramref name="VersionVerifiedAtUtc"/> is the never-verified state. It is a different state
/// from a version that was verified and whose instance has since stopped answering, so the two never
/// share one representation.
/// </para>
/// </remarks>
/// <param name="Address">The instance's base address, as it was saved.</param>
/// <param name="KeyIsSet">Whether a key is stored for this generation.</param>
/// <param name="RecordedVersion">
/// The version string a successful test against this stored address read, character for character.
/// </param>
/// <param name="VersionVerifiedAtUtc">When that version was read, or null when none ever was.</param>
/// <param name="LastReachableAtUtc">When this instance last answered anything at all.</param>
public sealed record WhisparrSyncGenerationSettingsView(
    string Address,
    bool KeyIsSet,
    string? RecordedVersion,
    DateTimeOffset? VersionVerifiedAtUtc,
    DateTimeOffset? LastReachableAtUtc);

/// <summary>Everything the settings page reads about the stored connections.</summary>
/// <remarks>
/// Discloses no API key, and cannot: no member of this type or of the types it carries can hold one.
/// That is a property of the shape rather than of the code that fills it, so a projection that read a
/// stored key would have nowhere to put it.
/// <para>
/// The two generations are carried side by side rather than one at a time, so selecting the other
/// generation and coming back returns the first unchanged with no second read.
/// </para>
/// </remarks>
/// <param name="SelectedGeneration">The generation the page is acting on.</param>
/// <param name="V3">The v3 connection.</param>
/// <param name="V2">The v2 connection.</param>
/// <param name="UpgradeBehavior">What a redelivery naming a different file does to the item.</param>
public sealed record WhisparrSyncSettingsView(
    WhisparrGeneration SelectedGeneration,
    WhisparrSyncGenerationSettingsView V3,
    WhisparrSyncGenerationSettingsView V2,
    UpgradeBehavior UpgradeBehavior);

/// <summary>What one save says about a generation's API key.</summary>
/// <remarks>
/// Three signals, because a form that submitted no key and a form asking for the stored key to be
/// removed are different requests. Encoding the difference as "a blank means keep" would make it a
/// convention nothing enforces.
/// <para>
/// The wire spelling is declared here, on the type. An equivalent converter in a serializer options
/// collection would outrank this one rather than duplicate it.
/// </para>
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum KeyWriteSignal
{
    /// <summary>Leave the stored key as it is. The value an absent signal takes.</summary>
    Keep,

    /// <summary>Store the submitted key. A submitted blank still keeps the stored key.</summary>
    Replace,

    /// <summary>Remove the stored key.</summary>
    Clear,
}

/// <summary>One generation's half of a settings save.</summary>
/// <remarks>
/// A generation this save omits entirely is left alone, which is what lets the page write the
/// generation it is showing without restating the other.
/// </remarks>
/// <param name="Address">The instance's base address, as the operator typed it.</param>
/// <param name="KeyWrite">Which of the three key writes this save is.</param>
/// <param name="ApiKey">The key to store, read only when <paramref name="KeyWrite"/> replaces.</param>
public sealed record WhisparrSyncGenerationSaveRequest(
    string? Address,
    KeyWriteSignal KeyWrite,
    string? ApiKey);

/// <summary>One settings save.</summary>
/// <remarks>
/// The key travels IN and never back out: nothing on the response side has a member that could carry
/// it.
/// </remarks>
/// <param name="SelectedGeneration">The generation the page is acting on.</param>
/// <param name="V3">The v3 half, or null to leave v3 alone.</param>
/// <param name="V2">The v2 half, or null to leave v2 alone.</param>
/// <param name="UpgradeBehavior">The upgrade behaviour to store, or null to leave it alone.</param>
public sealed record WhisparrSyncSettingsSaveRequest(
    WhisparrGeneration SelectedGeneration,
    WhisparrSyncGenerationSaveRequest? V3,
    WhisparrSyncGenerationSaveRequest? V2,
    UpgradeBehavior? UpgradeBehavior = null);
