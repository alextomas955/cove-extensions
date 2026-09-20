using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace WhisparrSync.Contracts;

/// <summary>How much of an entity's catalogue a monitor covers.</summary>
/// <remarks>Whisparr's own two names, spelled the same way on both generations.</remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum MonitorScope
{
    /// <summary>Future Scenes: monitor scenes that have not released yet.</summary>
    FutureScenes,

    /// <summary>All Scenes: monitor all scenes except specials.</summary>
    AllScenes,
}

/// <summary>Which kind of entity a monitor names.</summary>
/// <remarks>
/// The two generations address these kinds in namespaces neither shares with the other, so the kind
/// is carried beside an identifier rather than read out of one.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum WhisparrEntityKind
{
    Studio,
    Performer,
    Tag,
}

/// <summary>What a caller may say when it asks for an entity to be monitored.</summary>
/// <remarks>
/// A scope and nothing else. There is no identifier member of any kind, so which entity the outbound
/// request touches is not expressible in the request rather than being a value a validation step has
/// to refuse: the route names the Cove entity, and the identifier the instance is given is read from
/// the stored identity row on the server.
/// </remarks>
/// <param name="Scope">
/// How much of the entity's catalogue to cover, or null to take the product's own default. An
/// unrecognised spelling fails to bind, so no value can arrive as a default the caller did not name.
/// </param>
public sealed record MonitorEntityRequest(MonitorScope? Scope);

/// <summary>Why nothing was linked.</summary>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum ReflectOwnedSkipReason
{
    /// <summary>The instance's hard-link setting is off, so an import would copy the data.</summary>
    HardLinksOff,

    /// <summary>The setting could not be read, so whether an import would copy is unknown.</summary>
    HardLinkSettingUnreadable,
}

/// <summary>What asking for one entity's owned files to be reflected produced.</summary>
/// <remarks>
/// Exactly one of the three carries the answer: a refusal taken before anything was sent, a skip
/// decided from the instance's own setting, or the id of the run that was started.
/// <para>
/// The request has no body at all, so it carries no identifier member for the same reason
/// <see cref="MonitorEntityRequest"/> does not: the route names the Cove entity, and what the
/// instance is told is read on the server from the library's own rows.
/// </para>
/// </remarks>
public sealed record ReflectOwnedEnqueued(
    ReflectOwnedSkipReason? Skipped, string? JobId, MonitorRefusalKind Refusal);

/// <summary>What asking for one entity's missing scenes to be registered produced.</summary>
/// <remarks>
/// Exactly one of the two carries the answer: a refusal taken before anything was sent, or the id
/// of the run that was started.
/// <para>
/// The request has no body at all, so it carries no identifier member for the same reason
/// <see cref="MonitorEntityRequest"/> does not: the route names the Cove entity, and every
/// identifier the instance is given is read on the server from the library's own rows.
/// </para>
/// <para>
/// It carries no count either. What the run did is a line in the host's job list, and a count here
/// would be a number read before the run had offered anything.
/// </para>
/// </remarks>
public sealed record AddAllMissingEnqueued(string? JobId, MonitorRefusalKind Refusal);

/// <summary>Which verb one bulk gesture carries.</summary>
/// <remarks>
/// A verb is written down here only once a route serves it for a single entity. The selection bar
/// offers what the entity menu offers, so a verb reachable in bulk and nowhere else would be a
/// second answer to the same question.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum MonitorBulkVerb
{
    /// <summary>Monitor each selected entity, at the scope the request names.</summary>
    Monitor,

    /// <summary>Stop the connected instance monitoring each selected entity.</summary>
    Unmonitor,

    /// <summary>
    /// Ask the connected instance to look for what each selected entity monitors and does not hold.
    /// </summary>
    /// <remarks>
    /// The one verb here that can make an instance download. It changes no flag: an entity the
    /// instance does not monitor is searched over nothing, and the scope each entity is monitored at
    /// is left exactly as it stands.
    /// </remarks>
    SearchAllMonitored,
}

/// <summary>What a caller may say when it asks for a whole selection to be acted on.</summary>
/// <remarks>
/// The ids are Cove's own and nothing else identifying is expressible. Which entity each one names
/// on the instance is read from its stored identity row inside the batch, so an identifier a caller
/// put in the body reaches nothing.
/// <para>
/// Every member is nullable, the verb included. A non-nullable verb binds a body that names none to
/// the first member declared, which would make a selection acted on under a verb nobody named. Null
/// <c>Verb</c> is a refusal rather than a default: the verb decides what the request is.
/// </para>
/// <para>
/// <c>EntityType</c> arrives in the spelling the host's selection bar passed. The bar normalizes
/// only the two media plurals, so studios and performers arrive plural and are matched as they
/// arrive. <c>Scope</c> is null where the verb expresses no scope.
/// </para>
/// </remarks>
public sealed record MonitorBulkRequest(
    string? EntityType, MonitorBulkVerb? Verb, MonitorScope? Scope, int[]? EntityIds);

/// <summary>What one selected entity's turn in a batch produced.</summary>
public sealed record MonitorBulkOutcome(int CoveId, MonitorRefusalKind Refusal);

/// <summary>How one batch ended.</summary>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum MonitorBulkOutcomeKind
{
    /// <summary>Every selected entity had its turn.</summary>
    Completed,

    /// <summary>The batch was stopped part way, and what it had already done stands.</summary>
    Cancelled,

    /// <summary>There was nothing selected to act on, so nothing was done.</summary>
    NothingSelected,
}

/// <summary>One batch's per-entity outcomes, in the order the ids were supplied.</summary>
/// <remarks>
/// The order is the supplied one and is never grouped or sorted: a reader matches this list against
/// the selection they made, and a list ordered by outcome cannot be matched against anything.
/// <para>
/// It carries one entry per distinct id, so its length is bounded by the selection, which the route
/// caps before any of this runs.
/// </para>
/// </remarks>
public sealed record MonitorBulkRun(
    MonitorBulkOutcomeKind Outcome, IReadOnlyList<MonitorBulkOutcome> Outcomes)
{
    /// <summary>A batch that had nothing to act on.</summary>
    public static MonitorBulkRun NothingSelected { get; } =
        new(MonitorBulkOutcomeKind.NothingSelected, []);

    /// <summary>A batch every selected entity had its turn in.</summary>
    public static MonitorBulkRun Completed(IReadOnlyList<MonitorBulkOutcome> outcomes)
        => new(MonitorBulkOutcomeKind.Completed, outcomes);

    /// <summary>A batch stopped part way, keeping what it had already recorded.</summary>
    public static MonitorBulkRun Cancelled(IReadOnlyList<MonitorBulkOutcome> outcomes)
        => new(MonitorBulkOutcomeKind.Cancelled, outcomes);
}

/// <summary>Why a monitor could not be applied, or that it was.</summary>
/// <remarks>
/// One value per reason, never collapsed into a generic failure: each sends the user somewhere
/// different, and several are indistinguishable under the wrong test.
/// <para>
/// The backend answers with a kind, and the sentence a user reads is a frontend constant, so nothing
/// an instance said can reach the copy. That matters more here than elsewhere: this generation
/// answers a refused add with a body carrying a full stack trace.
/// </para>
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum MonitorRefusalKind
{
    /// <summary>Nothing was refused.</summary>
    None,

    /// <summary>No instance is configured, so no request was made.</summary>
    NotConfigured,

    /// <summary>
    /// The entity carries no identifier in the connected generation's own namespace, so there is
    /// nothing to name it by.
    /// </summary>
    /// <remarks>
    /// One kind whether the library holds no link at all or holds one only in the other generation's
    /// namespace. The namespace that counts is whichever the connected instance identifies entities
    /// in, and the sentence names the connected instance rather than a provider.
    /// </remarks>
    NoIdentityInThisNamespace,

    /// <summary>
    /// The entity carries several different identifiers in the connected generation's namespace, so
    /// which one names it is undecided.
    /// </summary>
    /// <remarks>
    /// A refusal rather than a first-row pick. The rows are matched by the host's own same-source
    /// rule, which treats two spellings of one provider as one source, so an entity can hold two
    /// matching rows carrying two different identifiers. Taking whichever came first would aim this
    /// extension's stored credential at whichever entity the row order happened to name.
    /// </remarks>
    SeveralIdentitiesInThisNamespace,

    /// <summary>The connected generation holds no capability that could honour this.</summary>
    CapabilityAbsentOnThisGeneration,

    /// <summary>The instance offers no quality profile, so no add can be composed.</summary>
    NoQualityProfile,

    /// <summary>The instance offers no library root, so no add can be composed.</summary>
    NoRootFolder,

    /// <summary>
    /// The library root this entity's own files sit under is one the instance has agreed no spelling
    /// for, so no add can be composed for it.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="NoRootFolder"/>, which is the instance offering no root at all. Here
    /// what is unsettled is which of those roots holds this entity's files, which is a folder mapping
    /// rather than a Whisparr setting.
    /// </remarks>
    NoAgreedRootForThisEntity,

    /// <summary>The instance answered, and would not do it.</summary>
    InstanceRefused,

    /// <summary>The instance answered, and the answer was larger than this product will read.</summary>
    /// <remarks>
    /// Distinct from <see cref="InstanceRefused"/> because the limit is this product's own, so a
    /// reader sent to look at the instance would find it answering correctly.
    /// </remarks>
    AnswerTooLargeToRead,

    /// <summary>The instance answered, and does not hold the entity.</summary>
    /// <remarks>
    /// Distinct from <see cref="InstanceRefused"/> because the instance declined nothing: it reported
    /// an absence. Only a read taken before anything was sent can state it; a read taken after an
    /// accepted write reports the change not arriving rather than an absence.
    /// </remarks>
    InstanceHoldsNoSuchEntity,

    /// <summary>The change was accepted, and the read taken straight after it does not report it.</summary>
    /// <remarks>
    /// Its own kind because it is the one refusal reached only after a write left. Every other kind
    /// can say nothing was changed; this one cannot. What the instance now holds is unknown here
    /// rather than unchanged, whether the read found no entity, found one it does not monitor, or
    /// could not be classified at all.
    /// </remarks>
    InstanceDidNotReportTheChange,
}

/// <summary>What one entity's monitoring looks like, as the entity page reads it.</summary>
/// <remarks>
/// Discloses no API key and no part of any response body: only a classified kind and the named
/// values a sentence needs.
/// <para>
/// It carries no count of any sort. A freshly added entity reports a catalogue of zero before any
/// refresh has run, so a count here would be a confident zero this product cannot support.
/// </para>
/// <para>
/// <c>Present</c> null is distinct from false: false says the instance was asked and holds nothing,
/// and null says nothing was established. <c>Scope</c> null is distinct from any particular scope:
/// it says the answer this read carried named none, so the browser must mark no scope at all rather
/// than fall back to a default. <c>Capabilities</c> is what the browser reads its menu from, rather
/// than a generation table of its own, so a capability that is absent is refused in one place.
/// </para>
/// </remarks>
public sealed record EntityMonitoringView(
    WhisparrEntityKind Kind,
    WhisparrGeneration? Generation,
    bool? Present,
    bool Monitored,
    MonitorRefusalKind Refusal,
    IReadOnlyList<WhisparrCapability> Capabilities,
    MonitorScope? Scope)
{
    /// <summary>A refusal taken before any instance was contacted, with nothing configured.</summary>
    public static EntityMonitoringView NotConfigured(WhisparrEntityKind kind)
        => new(kind, null, null, false, MonitorRefusalKind.NotConfigured, [], null);

    /// <summary>A refusal naming <paramref name="refusal"/>, with the entity left unmonitored.</summary>
    public static EntityMonitoringView Refused(
        WhisparrEntityKind kind,
        WhisparrGeneration generation,
        IReadOnlyList<WhisparrCapability> capabilities,
        MonitorRefusalKind refusal)
        => new(kind, generation, null, false, refusal, capabilities, null);

    /// <summary>The entity's state as the instance reports it.</summary>
    /// <remarks>
    /// <paramref name="scope"/> and <paramref name="present"/> have no defaults. A call site that
    /// never decided either question would otherwise answer a scope the instance did not report, or
    /// answer that the instance holds the entity on the strength of nothing.
    /// </remarks>
    public static EntityMonitoringView State(
        WhisparrEntityKind kind,
        WhisparrGeneration generation,
        IReadOnlyList<WhisparrCapability> capabilities,
        bool? present,
        bool monitored,
        MonitorScope? scope)
        => new(kind, generation, present, monitored, MonitorRefusalKind.None, capabilities, scope);
}
