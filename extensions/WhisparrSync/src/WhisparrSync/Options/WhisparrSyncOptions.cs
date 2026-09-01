using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Extensions.Shared;
using WhisparrSync.Contracts;
using WhisparrSync.Import;

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

/// <summary>What a redelivery naming a different file does to the item that already exists.</summary>
/// <remarks>
/// Neither value moves, renames or deletes a file in either system's storage. The wire spelling is
/// declared on the type; an equivalent converter in a serializer options collection would outrank it
/// rather than duplicate it.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum UpgradeBehavior
{
    /// <summary>Attach the new file to the existing item and touch nothing else.</summary>
    /// <remarks>
    /// The item lists both files until Cove's own scan notices the old one is gone.
    /// </remarks>
    Add,

    /// <summary>Attach the new file and detach the superseded file's row from the item.</summary>
    /// <remarks>
    /// The superseded file on disk is not touched and remains Whisparr's to remove.
    /// </remarks>
    Replace,
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

    /// <summary>Where the last backstop pass left off in this instance's history.</summary>
    /// <remarks>
    /// The date of the newest history record that pass saw. Null until a pass has run against this
    /// instance, and a pass that finds it null writes a fresh mark at the current position and
    /// imports nothing.
    /// <para>
    /// Rotating the API key does not reset it: the key lives in a table this record knows nothing
    /// about, and a save that leaves the address where it points keeps this whole connection. A save
    /// that moves the address replaces the connection, and the mark starts again, because a
    /// different address is a different instance with its own history.
    /// </para>
    /// </remarks>
    public DateTimeOffset? BackstopWatermarkUtc { get; init; }
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

/// <summary>The import channel's health, as state rather than as a journal.</summary>
/// <remarks>
/// A fixed member set: a thousand imports leave this the same size as one. It records the import
/// channel alone, which is the only part with a writer.
/// </remarks>
public sealed record ImportHealthAggregate
{
    /// <summary>The longest recorded failure text this keeps.</summary>
    public const int LastErrorMaxLength = 400;

    private readonly string _lastError = "";

    /// <summary>When an import last succeeded, or null when none has.</summary>
    public DateTimeOffset? LastWorkedAtUtc { get; init; }

    /// <summary>When an import last failed, or null when none has.</summary>
    public DateTimeOffset? LastFailedAtUtc { get; init; }

    /// <summary>The most recent failure's text, blank until one occurs.</summary>
    /// <remarks>
    /// Shortened to <see cref="LastErrorMaxLength"/> here rather than at each writer. An exception
    /// message has no length of its own, and this blob is served whole by the host's bulk
    /// extension-data route.
    /// </remarks>
    public string LastError
    {
        get => _lastError;
        init => _lastError = Shorten(value);
    }

    /// <summary>How many failures have followed the last success.</summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>Whether the backstop's stored position was lost, so a gap may exist.</summary>
    /// <remarks>
    /// Set by a pass that found no readable watermark and wrote a fresh one at the current position.
    /// The records between the lost position and that mark are never replayed; Cove's own library
    /// scan is what finds files already on disk.
    /// </remarks>
    public bool BackstopPositionLost { get; init; }

    private static string Shorten(string? text)
        => text is null || text.Length <= LastErrorMaxLength ? text ?? "" : text[..LastErrorMaxLength];
}

/// <summary>One offending path and why it was not imported.</summary>
public sealed record ImportRefusalEntry
{
    /// <summary>The longest reported path this stores.</summary>
    public const int PathMaxLength = 512;

    /// <summary>The path the delivery reported.</summary>
    public string Path { get; init; } = "";

    /// <summary>Why that path was refused.</summary>
    public ImportRefusalCause Cause { get; init; }
}

/// <summary>One Whisparr root's refusals since that root's last successful import.</summary>
/// <remarks>
/// <see cref="NewestPaths"/> holds at most <see cref="NewestPathsKept"/> entries. That is a
/// fixed-size design, not a cap on something that grows: a root's line is the same size whatever the
/// library holds, and the refusals are counted rather than listed.
/// <para>
/// <see cref="Root"/> is normalised on the way in, so two spellings of one root differing only by a
/// trailing separator are one entry rather than two.
/// </para>
/// </remarks>
public sealed record ImportRootRefusals
{
    /// <summary>How many of the newest offending paths one root's line holds.</summary>
    public const int NewestPathsKept = 3;

    private readonly string _root = "";

    /// <summary>The Whisparr root the refused path fell under.</summary>
    public string Root
    {
        get => _root;
        init => _root = NormaliseRoot(value);
    }

    /// <summary>How many refusals this root has had since its last successful import.</summary>
    public int CountSinceLastSuccess { get; init; }

    /// <summary>The newest offending paths, newest first.</summary>
    public List<ImportRefusalEntry> NewestPaths { get; init; } = [];

    /// <summary>
    /// <paramref name="root"/> in the one spelling this aggregate stores it under.
    /// </summary>
    /// <remarks>
    /// Trailing separators are dropped, in both spellings, so a reporter that appends one and a
    /// reporter that does not produce the same entry. A root that is nothing but separators keeps
    /// one, so the root of a filesystem stays addressable. Case is left alone: on the platforms
    /// these paths come from, two casings are two different roots.
    /// </remarks>
    /// <param name="root">The root as it was reported.</param>
    public static string NormaliseRoot(string? root)
    {
        if (string.IsNullOrEmpty(root))
        {
            return "";
        }

        var trimmed = root.TrimEnd('/', '\\');
        return trimmed.Length == 0 ? root[..1] : trimmed;
    }

    // Record value equality compares the List member by reference, so a JSON round-trip, which
    // allocates a fresh list, would never be Equal to the original. Both Equals and GetHashCode run
    // off the SAME component list, which yields the paths element by element.
    public bool Equals(ImportRootRefusals? other)
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
        yield return Root;
        yield return CountSinceLastSuccess;

        // The count precedes the paths so two component streams cannot line up by borrowing a member
        // from either side of the list.
        yield return NewestPaths.Count;
        foreach (var path in NewestPaths)
        {
            yield return path;
        }
    }
}

/// <summary>
/// Everything Whisparr Sync persists except the API key, as one bounded JSON blob under the store's
/// <c>options</c> key.
/// </summary>
/// <remarks>
/// Every member here is a scalar or a collection whose length is fixed by design, so the whole blob
/// stays O(1) in the size of the library. Nothing per-file, per-entity or per-scene may join it:
/// Cove's bulk extension-data route serialises every value an extension owns, so one oversized value
/// fails the whole settings page.
/// <para>
/// The API key is deliberately absent, and so is the inbound callback secret. Both live in a table
/// this extension owns, which that same bulk route cannot reach.
/// </para>
/// </remarks>
public sealed record WhisparrSyncOptions
{
    /// <summary>The interval a backstop pass runs at until something changes it.</summary>
    public const int DefaultBackstopIntervalSeconds = 900;

    /// <summary>The shortest interval a stored value is honoured at.</summary>
    /// <remarks>
    /// A pass cannot run more often than the worker wakes, and the worker builds its wake period from
    /// this.
    /// </remarks>
    public const int BackstopIntervalFloorSeconds = 30;

    /// <summary>The generation the settings page is acting on.</summary>
    public WhisparrGeneration SelectedGeneration { get; init; } = WhisparrGeneration.V3;

    /// <summary>The v3 connection, or null when v3 has never been configured.</summary>
    public WhisparrSyncGenerationConnection? V3 { get; init; }

    /// <summary>The v2 connection, or null when v2 has never been configured.</summary>
    public WhisparrSyncGenerationConnection? V2 { get; init; }

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

    /// <summary>What a redelivery naming a different file does to the existing item.</summary>
    /// <remarks>
    /// Defaults to <see cref="UpgradeBehavior.Add"/>, which attaches the new file and touches nothing
    /// else. Neither value moves, renames or deletes a file in either system's storage.
    /// </remarks>
    public UpgradeBehavior UpgradeBehavior { get; init; } = UpgradeBehavior.Add;

    /// <summary>How long a backstop pass waits before the next one, in seconds.</summary>
    /// <remarks>
    /// Settable so the containerized end-to-end suite can drive the whole backstop path in a real
    /// container rather than against a fake clock. Read through <see cref="BackstopInterval"/>, which
    /// applies the floor.
    /// </remarks>
    public int BackstopIntervalSeconds { get; init; } = DefaultBackstopIntervalSeconds;

    /// <summary>The interval a backstop pass is actually gated on.</summary>
    /// <remarks>
    /// The floor is applied here, on the read, rather than by refusing a save, so a value that never
    /// passed through a save is still floored.
    /// </remarks>
    [JsonIgnore]
    public TimeSpan BackstopInterval
        => TimeSpan.FromSeconds(Math.Max(BackstopIntervalSeconds, BackstopIntervalFloorSeconds));

    /// <summary>The import channel's health.</summary>
    public ImportHealthAggregate ImportHealth { get; init; } = new();

    /// <summary>The refusals outstanding, one entry per Whisparr root that has any.</summary>
    /// <remarks>
    /// Empty while every root's last import succeeded. A root's entry is removed by its own next
    /// success, so a half-broken setup keeps the roots that are still failing and loses the ones that
    /// are not. The Whisparr root count is a handful, and each entry is a fixed size.
    /// </remarks>
    public List<ImportRootRefusals> ImportRefusals { get; init; } = [];

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
    // off the SAME component list, which yields the refusals element by element.
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
        yield return UpgradeBehavior;
        yield return BackstopIntervalSeconds;
        yield return ImportHealth;

        // The count precedes the entries so two component streams cannot line up by borrowing a
        // member from either side of the list.
        yield return ImportRefusals.Count;
        foreach (var refusals in ImportRefusals)
        {
            yield return refusals;
        }
    }
}
