using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Extensions.Shared;
using WhisparrSync.Contracts;

namespace WhisparrSync.Options;

/// <summary>How much of an entity's catalogue monitoring arms.</summary>
/// <remarks>
/// Neither scope searches or downloads. The wire spelling is declared on the type; an equivalent
/// converter in a serializer options collection would outrank it rather than duplicate it.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum MonitorScope
{
    /// <summary>Future scenes are wanted; the back-catalogue stays visible but unarmed.</summary>
    NewReleasesOnly,

    /// <summary>
    /// Everything the entity offers becomes wanted, including scenes the user already owns, which
    /// Whisparr has no file for and will therefore try to re-acquire.
    /// </summary>
    AllScenes,
}

/// <summary>One prefix rewrite between the path Cove sees and the path Whisparr sees.</summary>
/// <remarks>
/// Matched at a path-segment boundary, so <c>/media/films</c> never matches <c>/media/filmstrip</c>.
/// </remarks>
public sealed record PathTranslationRule
{
    /// <summary>The leading path Cove holds the library under.</summary>
    public string CovePrefix { get; init; } = "";

    /// <summary>The leading path Whisparr holds the same library under.</summary>
    public string WhisparrPrefix { get; init; } = "";
}

/// <summary>
/// One generation's stored connection. Absent for a generation that has never been configured.
/// </summary>
/// <remarks>
/// Each generation carries its own instance, because selecting the other generation and coming back
/// has to return this one unchanged.
/// <para>
/// The two recorded instants measure different things and are never derived from one another: the
/// version reading is as old as the test that produced it, while reachability is as recent as the
/// last answer of any kind.
/// </para>
/// </remarks>
public sealed record WhisparrSyncGenerationConnection
{
    /// <summary>The instance's base address, as it was saved.</summary>
    public string Address { get; init; } = "";

    /// <summary>The version string the instance sent, character for character.</summary>
    public string? RecordedVersion { get; init; }

    /// <summary>When a test against this stored address read that version.</summary>
    public DateTimeOffset? VersionVerifiedAtUtc { get; init; }

    /// <summary>When this instance last answered anything at all.</summary>
    public DateTimeOffset? LastReachableAtUtc { get; init; }

    /// <summary>Whether this product's callback is registered here, as of the last check.</summary>
    /// <remarks>
    /// Starts at <see cref="RegistrationStatus.NotCheckedYet"/> and is only moved off it by a read of
    /// this instance's own notification list. A generation the user has never checked therefore
    /// answers "not checked yet" rather than borrowing the other generation's answer.
    /// </remarks>
    public RegistrationStatus CallbackRegistration { get; init; } = RegistrationStatus.NotCheckedYet;

    /// <summary>Where the most recent inbound callback from this instance carried its secret.</summary>
    /// <remarks>
    /// Null until one arrives, which is what distinguishes "registered, no events received yet" from
    /// "registered and delivering". The note about the less private form is shown while this reads
    /// <see cref="CallbackSecretPosition.Address"/>, and clears itself when an event arrives out of
    /// band.
    /// </remarks>
    public CallbackSecretPosition? LastCallbackSecretPosition { get; init; }
}

/// <summary>
/// Which metadata provider configured in Cove counts as the identity source, per generation.
/// </summary>
/// <remarks>
/// A blank slot means the provider's standard address. Only a Cove whose provider sits at a
/// non-standard address needs one filled in.
/// </remarks>
public sealed record MetadataProviderEndpoints
{
    /// <summary>The endpoint the v3 generation resolves identities against.</summary>
    public string V3 { get; init; } = "";

    /// <summary>The endpoint the v2 generation resolves identities against.</summary>
    public string V2 { get; init; } = "";
}

/// <summary>
/// Everything Whisparr Sync persists except the API key, as one bounded JSON blob under the store's
/// <c>options</c> key.
/// </summary>
/// <remarks>
/// Every member here is a scalar or a rule list an operator writes by hand, so the whole blob stays
/// O(1) in the size of the library. Nothing per-file, per-entity or per-scene may join it: Cove's
/// bulk extension-data route serialises every value an extension owns, so one oversized value fails
/// the whole settings page.
/// <para>
/// The API key is deliberately absent. It lives in a table this extension owns, which that same bulk
/// route cannot reach.
/// </para>
/// </remarks>
public sealed record WhisparrSyncOptions
{
    /// <summary>The generation the settings page is acting on.</summary>
    public WhisparrGeneration SelectedGeneration { get; init; } = WhisparrGeneration.V3;

    /// <summary>The v3 connection, or null when v3 has never been configured.</summary>
    public WhisparrSyncGenerationConnection? V3 { get; init; }

    /// <summary>The v2 connection, or null when v2 has never been configured.</summary>
    public WhisparrSyncGenerationConnection? V2 { get; init; }

    /// <summary>A prefix-rewrite table for setups where the two systems cannot see the library at one path.</summary>
    /// <remarks>
    /// The escape hatch for a deployment whose mounts genuinely differ. First matching rule wins.
    /// Empty by default, which is the case where both systems already agree on every path.
    /// </remarks>
    public List<PathTranslationRule> PathTranslation { get; init; } = [];

    /// <summary>The monitor scope used when a caller does not specify one.</summary>
    /// <remarks>
    /// Defaults to <see cref="MonitorScope.NewReleasesOnly"/>, which leaves the existing
    /// back-catalogue unarmed. Both scopes stay non-grabbing whatever this is set to.
    /// </remarks>
    public MonitorScope DefaultMonitorScope { get; init; } = MonitorScope.NewReleasesOnly;

    /// <summary>Which metadata provider counts as the identity source, per generation.</summary>
    /// <remarks>
    /// Both slots blank by default, meaning each provider's standard address.
    /// </remarks>
    public MetadataProviderEndpoints MetadataProviderEndpoints { get; init; } = new();

    /// <summary>The host the callback address is built on before a registration exists.</summary>
    /// <remarks>
    /// Never typed directly: it is stored from the address the user edited, which is what makes that
    /// edit survive a refresh. Blank by default, meaning the host is derived from the request.
    /// </remarks>
    public string CallbackHost { get; init; } = "";

    /// <summary>The connection stored for <paramref name="generation"/>, or null when none is.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="generation"/> is not one this record carries a slot for.
    /// </exception>
    public WhisparrSyncGenerationConnection? ConnectionFor(WhisparrGeneration generation)
        => generation switch
        {
            WhisparrGeneration.V3 => V3,
            WhisparrGeneration.V2 => V2,
            _ => throw new ArgumentOutOfRangeException(nameof(generation), generation, null),
        };

    /// <summary>This record with <paramref name="generation"/>'s connection replaced.</summary>
    /// <remarks>The other generation is carried through untouched.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="generation"/> is not one this record carries a slot for.
    /// </exception>
    public WhisparrSyncOptions WithConnectionFor(
        WhisparrGeneration generation, WhisparrSyncGenerationConnection connection)
        => generation switch
        {
            WhisparrGeneration.V3 => this with { V3 = connection },
            WhisparrGeneration.V2 => this with { V2 = connection },
            _ => throw new ArgumentOutOfRangeException(nameof(generation), generation, null),
        };

    /// <summary>
    /// Shared serializer settings used by both save and load, so the round-trip is symmetric.
    /// </summary>
    /// <remarks>
    /// Case-insensitive, which keeps a hand-edited blob readable. It carries no enum converter: each
    /// enum declares its own spelling on the type, and a converter here would outrank that
    /// declaration rather than agree with it.
    /// </remarks>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Record value equality compares List members by reference, so a JSON round-trip — which
    // allocates a fresh list — would never be Equal to the original. Both Equals and GetHashCode run
    // off the SAME component list, which yields the rules element by element.
    public bool Equals(WhisparrSyncOptions? other)
        => other is not null && EqualityComponents().SequenceEqual(other.EqualityComponents());

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var component in EqualityComponents())
        {
            hash.Add(component);
        }

        return hash.ToHashCode();
    }

    private IEnumerable<object?> EqualityComponents()
    {
        yield return SelectedGeneration;
        yield return V3;
        yield return V2;
        yield return DefaultMonitorScope;
        yield return MetadataProviderEndpoints;
        yield return CallbackHost;

        // The count precedes the rules so two component streams cannot line up by borrowing a member
        // from either side of the list.
        yield return PathTranslation.Count;
        foreach (var rule in PathTranslation)
        {
            yield return rule;
        }
    }
}
