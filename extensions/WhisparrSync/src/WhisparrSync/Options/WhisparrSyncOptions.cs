using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Extensions.Shared;
using WhisparrSync.Contracts;

namespace WhisparrSync.Options;

// Shortens a string whose length something outside this product decides. Each caller declares its
// own ceiling.
internal static class BoundedText
{
    // A null is returned as null, so a member that distinguishes "never set" from "empty" keeps
    // that distinction and a member that does not coalesces at its own accessor.
    internal static string? Shorten(string? text, int maxLength)
        => text is null || text.Length <= maxLength ? text : text[..maxLength];
}

/// <summary>
/// Reads the withdrawn spelling of the narrower monitor scope, and writes only the current one.
/// </summary>
/// <remarks>
/// A blob written before the two scope enums were collapsed into one names the narrower scope by a
/// word the surviving enum does not declare. A blob the store cannot bind reads as defaults
/// everywhere above, so one unrecognised word would reset an install's address, endpoints and
/// callback host as well as its scope.
/// <para>
/// Declared on the property. The enum type's own attribute is the wire spelling for every other use
/// of the enum and must not be widened to accept a word no wire document declares, and an entry in
/// a serializer options collection would outrank that attribute rather than agree with it.
/// </para>
/// <para>
/// Any spelling this does not recognise reads as the narrower scope: choosing the wide one wrongly
/// marks a whole back catalogue wanted, which on v3 narrowing the scope again does not undo.
/// </para>
/// <para>
/// Temporary. Once no stored blob anywhere can carry the withdrawn word, this converter and its
/// property attribute can be deleted.
/// </para>
/// </remarks>
public sealed class WithdrawnMonitorScopeSpelling : JsonConverter<MonitorScope>
{
    private const string WithdrawnNarrowScope = "newReleasesOnly";

    public override MonitorScope Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return MonitorScope.FutureScenes;
        }

        var stored = reader.GetString();
        return string.Equals(stored, WithdrawnNarrowScope, StringComparison.OrdinalIgnoreCase)
            || !Enum.TryParse<MonitorScope>(stored, ignoreCase: true, out var named)
            || !Enum.IsDefined(named)
                ? MonitorScope.FutureScenes
                : named;
    }

    public override void Write(
        Utf8JsonWriter writer, MonitorScope value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(
            JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
    }
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
    /// <summary>
    /// Attach the new file only. The item lists both files until Cove's own scan notices the old one
    /// is gone.
    /// </summary>
    Add,

    /// <summary>Attach the new file and detach the superseded file's row from the item.</summary>
    Replace,
}

/// <summary>
/// One generation's stored connection. Absent for a generation that has never been configured.
/// </summary>
/// <remarks>
/// Each generation carries its own instance, because selecting the other generation and coming back
/// has to return this one unchanged.
/// <para>
/// <see cref="VersionVerifiedAtUtc"/> is as old as the test that produced the version reading, while
/// <see cref="LastReachableAtUtc"/> is as recent as the last answer of any kind. Neither is derived
/// from the other.
/// </para>
/// </remarks>
public sealed record WhisparrSyncGenerationConnection
{
    /// <summary>The longest reported version string this keeps.</summary>
    public const int RecordedVersionMaxLength = 64;

    private readonly string? _recordedVersion;

    public string Address { get; init; } = "";

    /// <summary>The version string the instance sent, or null while no test has read one.</summary>
    /// <remarks>
    /// Shortened to <see cref="RecordedVersionMaxLength"/> here rather than at the write that records
    /// it. The instance decides how long its own version string is, and this blob is served whole by
    /// the host's bulk extension-data route.
    /// </remarks>
    public string? RecordedVersion
    {
        get => _recordedVersion;
        init => _recordedVersion = BoundedText.Shorten(value, RecordedVersionMaxLength);
    }

    public DateTimeOffset? VersionVerifiedAtUtc { get; init; }

    public DateTimeOffset? LastReachableAtUtc { get; init; }

    /// <summary>Whether this product's callback is registered here, as of the last check.</summary>
    /// <remarks>
    /// Moved off <see cref="RegistrationStatus.NotCheckedYet"/> only by a read of this instance's own
    /// notification list, so a generation the user has never checked does not borrow the other
    /// generation's answer.
    /// </remarks>
    public RegistrationStatus CallbackRegistration { get; init; } = RegistrationStatus.NotCheckedYet;

    /// <summary>Where the most recent inbound callback from this instance carried its secret.</summary>
    /// <remarks>
    /// Null until one arrives, which distinguishes "registered, no events received yet" from
    /// "registered and delivering".
    /// </remarks>
    public CallbackSecretPosition? LastCallbackSecretPosition { get; init; }

    /// <summary>The date of the newest history record the last backstop pass saw.</summary>
    /// <remarks>
    /// Null until a pass has run against this instance. A pass that finds it null writes a fresh mark
    /// at the current position and imports nothing.
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
    public string V3 { get; init; } = "";

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

    /// <summary>The longest failure streak this counts to.</summary>
    public const int ConsecutiveFailuresCeiling = 10;

    private readonly string _lastError = "";
    private readonly int _consecutiveFailures;

    public DateTimeOffset? LastWorkedAtUtc { get; init; }

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
        init => _lastError = BoundedText.Shorten(value, LastErrorMaxLength) ?? "";
    }

    /// <summary>How many failures have followed the last success, counted to a ceiling.</summary>
    /// <remarks>
    /// Held to <see cref="ConsecutiveFailuresCeiling"/> here rather than at each writer, as
    /// <see cref="LastError"/> is shortened. Past the ceiling the exact length says only that the
    /// same failure is still recurring.
    /// </remarks>
    public int ConsecutiveFailures
    {
        get => _consecutiveFailures;
        init => _consecutiveFailures = Math.Min(value, ConsecutiveFailuresCeiling);
    }

    /// <summary>Whether the backstop's stored position was lost, so a gap may exist.</summary>
    /// <remarks>
    /// Set by a pass that found no readable watermark and wrote a fresh one at the current position.
    /// The records between the lost position and that mark are never replayed; Cove's own library
    /// scan is what finds files already on disk.
    /// </remarks>
    public bool BackstopPositionLost { get; init; }

    /// <summary>How many history records a backstop pass could not take, over every pass.</summary>
    /// <remarks>
    /// A running total rather than a streak: the mark moves past a contained record, so this channel
    /// never offers it again and no later success clears the total.
    /// </remarks>
    public int RecordsContained { get; init; }

    /// <summary>When a pass last could not take a record, or null when none ever has.</summary>
    /// <remarks>
    /// Recorded because <see cref="RecordsContained"/> never clears, so the total alone cannot say
    /// whether the containment is still happening.
    /// </remarks>
    public DateTimeOffset? LastContainedAtUtc { get; init; }
}

/// <summary>One offending path and why it was not imported.</summary>
public sealed record ImportRefusalEntry
{
    /// <summary>The longest reported path this keeps.</summary>
    public const int PathMaxLength = 512;

    private readonly string _path = "";

    /// <summary>The path the delivery reported.</summary>
    /// <remarks>
    /// Shortened to <see cref="PathMaxLength"/> here rather than at each writer. A reported path has
    /// no length of its own, and this blob is served whole by the host's bulk extension-data route.
    /// </remarks>
    public string Path
    {
        get => _path;
        init => _path = BoundedText.Shorten(value, PathMaxLength) ?? "";
    }

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
    private readonly List<ImportRefusalEntry> _newestPaths = [];

    public string Root
    {
        get => _root;
        init => _root = NormaliseRoot(value);
    }

    public int CountSinceLastSuccess { get; init; }

    /// <summary>The newest offending paths, newest first.</summary>
    /// <remarks>
    /// Emptied here rather than by an initialiser, which runs only for an absent key: a stored blob
    /// naming this member as null binds it as null, and the load path's non-null restore does not
    /// descend into a collection's elements to replace it.
    /// </remarks>
    public List<ImportRefusalEntry> NewestPaths
    {
        get => _newestPaths;
        init => _newestPaths = value ?? [];
    }

    /// <summary>
    /// <paramref name="root"/> in the one spelling this aggregate stores it under.
    /// </summary>
    /// <remarks>
    /// Trailing separators are dropped, in both spellings, so a reporter that appends one and a
    /// reporter that does not produce the same entry. A root that is nothing but separators keeps
    /// one, so the root of a filesystem stays addressable. Case is left alone: on the platforms
    /// these paths come from, two casings are two different roots.
    /// </remarks>
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
    // off the same component list.
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

/// <summary>One Cove library root the connected instance established no path for.</summary>
/// <remarks>
/// <see cref="PathsTried"/> holds at most <see cref="PathsTriedKept"/> entries. That is a fixed-size
/// design, not a cap on something that grows: the paths asked about under one root are one per root
/// the instance declares plus the library's own spelling, and the folders under the root do not enter
/// into it.
/// <para>
/// <see cref="Root"/> is normalised on the way in, so two spellings of one root differing only by a
/// trailing separator are one entry rather than two.
/// </para>
/// </remarks>
public sealed record OutboundRootRefusal
{
    /// <summary>How many of the paths asked about one root's entry holds.</summary>
    public const int PathsTriedKept = 3;

    private readonly string _root = "";
    private readonly List<string> _pathsTried = [];

    public string Root
    {
        get => _root;
        init => _root = ImportRootRefusals.NormaliseRoot(value);
    }

    public FolderAgreementRefusal Refusal { get; init; }

    /// <summary>The paths the instance was asked about, in the order they were formed.</summary>
    /// <remarks>
    /// Emptied and bounded here rather than by an initialiser, which runs only for an absent key: a
    /// stored blob naming this member as null binds it as null, and the load path's non-null restore
    /// does not descend into a collection's elements to replace it.
    /// </remarks>
    public List<string> PathsTried
    {
        get => _pathsTried;
        init => _pathsTried = value is null || value.Count <= PathsTriedKept
            ? value ?? []
            : [.. value.Take(PathsTriedKept)];
    }

    // Record value equality compares the List member by reference, so a JSON round-trip, which
    // allocates a fresh list, would never be Equal to the original. Both Equals and GetHashCode run
    // off the same component list.
    public bool Equals(OutboundRootRefusal? other)
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
        yield return Refusal;

        // The count precedes the paths so two component streams cannot line up by borrowing a member
        // from either side of the list.
        yield return PathsTried.Count;
        foreach (var path in PathsTried)
        {
            yield return path;
        }
    }
}

/// <summary>Where an operator states one Cove library root is on the connected instance.</summary>
/// <remarks>
/// A candidate rather than a fact: the probe decides on the save and on every later run alike, so a
/// mapping stored here is never a path anything is handed on trust.
/// <para>
/// Both roots are normalised on the way in, so a path typed with a trailing separator keys and
/// rebuilds the same way one typed without it does.
/// </para>
/// </remarks>
public sealed record OutboundRootMapping
{
    private readonly string _coveRoot = "";
    private readonly string _instanceRoot = "";

    public string CoveRoot
    {
        get => _coveRoot;
        init => _coveRoot = ImportRootRefusals.NormaliseRoot(value);
    }

    /// <summary>Where the instance holds that root, as the operator stated it.</summary>
    public string InstanceRoot
    {
        get => _instanceRoot;
        init => _instanceRoot = ImportRootRefusals.NormaliseRoot(value);
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
    public const int DefaultBackstopIntervalSeconds = 900;

    private readonly List<OutboundRootRefusal> _outboundRefusals = [];
    private readonly List<OutboundRootMapping> _outboundMappings = [];

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
    /// The default leaves the existing back catalogue unarmed. Both scopes stay non-grabbing whatever
    /// this is set to.
    /// </remarks>
    [JsonConverter(typeof(WithdrawnMonitorScopeSpelling))]
    public MonitorScope DefaultMonitorScope { get; init; } = MonitorScope.FutureScenes;

    /// <summary>Which metadata provider counts as the identity source, per generation.</summary>
    /// <remarks>Both slots blank by default, meaning each provider's standard address.</remarks>
    public MetadataProviderEndpoints MetadataProviderEndpoints { get; init; } = new();

    /// <summary>The host the callback address is built on before a registration exists.</summary>
    /// <remarks>
    /// Never typed directly: it is stored from the address the user edited, which is what makes that
    /// edit survive a refresh. Blank means the host is derived from the request.
    /// </remarks>
    public string CallbackHost { get; init; } = "";

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
    /// The floor is applied on the read rather than by refusing a save, so a value that never passed
    /// through a save is still floored.
    /// </remarks>
    [JsonIgnore]
    public TimeSpan BackstopInterval
        => TimeSpan.FromSeconds(Math.Max(BackstopIntervalSeconds, BackstopIntervalFloorSeconds));

    public ImportHealthAggregate ImportHealth { get; init; } = new();

    /// <summary>The refusals outstanding, one entry per Whisparr root that has any.</summary>
    /// <remarks>
    /// A root's entry is removed by its own next success. Bounded by the Whisparr root count, and
    /// each entry is a fixed size.
    /// </remarks>
    public List<ImportRootRefusals> ImportRefusals { get; init; } = [];

    /// <summary>
    /// One entry per Cove library root the last run over it established no instance path for.
    /// </summary>
    /// <remarks>
    /// A root a run addressed loses its entry. Bounded by the host's library root count, which an
    /// operator created by hand.
    /// <para>
    /// Emptied at the accessor rather than by an initialiser, so a stored blob naming this member as
    /// null binds it as empty.
    /// </para>
    /// </remarks>
    public List<OutboundRootRefusal> OutboundRefusals
    {
        get => _outboundRefusals;
        init => _outboundRefusals = value ?? [];
    }

    /// <summary>
    /// One entry per Cove library root an operator has stated the instance's own path for.
    /// </summary>
    /// <remarks>
    /// Read in place of the roots the instance declares, never alongside them. Still only a candidate:
    /// the probe decides on the save and on every later run. Bounded by the host's library root count.
    /// Emptied at the accessor for the same reason <see cref="OutboundRefusals"/> is.
    /// </remarks>
    public List<OutboundRootMapping> OutboundMappings
    {
        get => _outboundMappings;
        init => _outboundMappings = value ?? [];
    }

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
    /// declaration.
    /// </remarks>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Record value equality compares List members by reference, so a JSON round-trip, which
    // allocates a fresh list, would never be Equal to the original. Both Equals and GetHashCode run
    // off the same component list.
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

        yield return OutboundRefusals.Count;
        foreach (var refusal in OutboundRefusals)
        {
            yield return refusal;
        }

        yield return OutboundMappings.Count;
        foreach (var mapping in OutboundMappings)
        {
            yield return mapping;
        }
    }
}
