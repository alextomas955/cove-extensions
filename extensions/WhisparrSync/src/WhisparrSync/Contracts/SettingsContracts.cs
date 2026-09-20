using System.Text.Json.Serialization;
using Cove.Extensions.Shared;
using WhisparrSync.Options;

namespace WhisparrSync.Contracts;

/// <summary>One generation's stored connection, as the settings page reads it.</summary>
/// <remarks>
/// <c>VersionVerifiedAtUtc</c> is separate from <c>LastReachableAtUtc</c> because the two readings
/// measure different things: a version is as old as the test that read it, while reachability is as
/// recent as the last answer of any kind. Null <c>VersionVerifiedAtUtc</c> is the never-verified
/// state, which is a different state from a version that was verified and whose instance has since
/// stopped answering.
/// <para>
/// <c>RecordedVersion</c> is the version string a successful test read, character for character.
/// </para>
/// </remarks>
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
/// <c>UpgradeBehavior</c> is what a redelivery naming a different file does to the item.
/// </para>
/// </remarks>
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
/// generation it is showing without restating the other. <c>ApiKey</c> is read only when
/// <c>KeyWrite</c> is <see cref="KeyWriteSignal.Replace"/>.
/// </remarks>
public sealed record WhisparrSyncGenerationSaveRequest(
    string? Address,
    KeyWriteSignal KeyWrite,
    string? ApiKey);

/// <summary>One settings save.</summary>
/// <remarks>
/// The key travels in and never back out: nothing on the response side has a member that could
/// carry it. A null generation half, or a null <c>UpgradeBehavior</c>, leaves that value alone.
/// </remarks>
public sealed record WhisparrSyncSettingsSaveRequest(
    WhisparrGeneration SelectedGeneration,
    WhisparrSyncGenerationSaveRequest? V3,
    WhisparrSyncGenerationSaveRequest? V2,
    UpgradeBehavior? UpgradeBehavior = null);
