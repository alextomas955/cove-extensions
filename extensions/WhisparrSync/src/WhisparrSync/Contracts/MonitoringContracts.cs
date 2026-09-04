using System.Text.Json.Serialization;
using Cove.Extensions.Shared;
using WhisparrSync.Monitoring;

namespace WhisparrSync.Contracts;

/// <summary>What a caller may say when it asks for an entity to be monitored.</summary>
/// <remarks>
/// A scope and nothing else. There is no identifier member of any kind, so which entity the outbound
/// request touches is not expressible in the request at all rather than being a value a validation
/// step has to refuse: the route names the Cove entity, and the identifier the instance is given is
/// read from the stored identity row on the server.
/// </remarks>
/// <param name="Scope">
/// How much of the entity's catalogue to cover, or null to take the product's own default. An
/// unrecognised spelling fails to bind, so no value can arrive as a default the caller did not name.
/// </param>
public sealed record MonitorEntityRequest(MonitorScope? Scope);

/// <summary>Why nothing was linked.</summary>
/// <remarks>
/// A wire type, because the sentence a user reads is chosen in the browser from the kind answered
/// here. The converter is on the TYPE: an options-level one outranks a type attribute, so a second
/// declaration could drift and win in silence.
/// </remarks>
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
/// <param name="Skipped">Why nothing was asked for, or null when something was.</param>
/// <param name="JobId">The run this extension's own status route answers about, or null.</param>
/// <param name="Refusal">Why nothing could be started, or that something was.</param>
public sealed record ReflectOwnedEnqueued(
    ReflectOwnedSkipReason? Skipped, string? JobId, MonitorRefusalKind Refusal);

/// <summary>What asking for one entity's missing scenes to be registered produced.</summary>
/// <remarks>
/// Exactly one of the two carries the answer: a refusal taken before anything was sent, or the id
/// of the run that was started.
/// <para>
/// The request has no body at all, so it carries no identifier member for the same reason
/// <see cref="MonitorEntityRequest"/> does not: the route names the Cove entity, and every
/// identifier the instance is given - the entity's and each scene's - is read on the server from the
/// library's own rows.
/// </para>
/// <para>
/// It carries no count either. What the run did is a line in the host's job list, and a count here
/// would be a number read before the run had offered anything.
/// </para>
/// </remarks>
/// <param name="JobId">The run this extension's own status route answers about, or null.</param>
/// <param name="Refusal">Why nothing could be started, or that something was.</param>
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
}

/// <summary>What a caller may say when it asks for a whole selection to be acted on.</summary>
/// <remarks>
/// The ids are Cove's own and nothing else identifying is expressible. Which entity each one names
/// on the instance is read from its stored identity row inside the batch, so the same rule holds
/// here as on the single-entity routes: an identifier a caller put in the body reaches nothing.
/// <para>
/// Every member is nullable, the verb included. A non-nullable verb binds a body that names none to
/// the first member declared, which would make a selection acted on under a verb nobody named.
/// </para>
/// </remarks>
/// <param name="EntityType">
/// The type the host's selection bar passed, in the spelling it passed it. The bar normalizes only
/// the two media plurals, so studios and performers arrive plural and are matched as they arrive.
/// </param>
/// <param name="Verb">
/// Which gesture to carry out for every selected entity. Null is a refusal rather than a default:
/// the verb decides what the request is, so there is nothing for an unnamed one to fall back to.
/// </param>
/// <param name="Scope">
/// How much of each entity's catalogue to cover, or null where the verb expresses no scope.
/// </param>
/// <param name="EntityIds">The Cove ids selected.</param>
public sealed record MonitorBulkRequest(
    string? EntityType, MonitorBulkVerb? Verb, MonitorScope? Scope, int[]? EntityIds);

/// <summary>What one selected entity's turn in a batch produced.</summary>
/// <param name="CoveId">The Cove entity this outcome is about.</param>
/// <param name="Refusal">Why it could not be done for this entity, or that it was done.</param>
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
/// It carries one entry per DISTINCT id, so its length is bounded by the selection, which the route
/// caps before any of this runs.
/// </para>
/// </remarks>
/// <param name="Outcome">How the batch ended.</param>
/// <param name="Outcomes">One entry per distinct selected entity, in the supplied order.</param>
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
/// The backend answers with a KIND. The sentence a user reads is a frontend constant, so nothing an
/// instance said can reach the copy. That matters more here than elsewhere: this generation answers a
/// refused add with a body carrying a full stack trace.
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
    /// namespace. The two are the same fact from the reader's side, because the namespace that counts
    /// is whichever the connected instance identifies entities in, and the sentence names the
    /// connected instance rather than a provider.
    /// </remarks>
    NoIdentityInThisNamespace,

    /// <summary>
    /// The entity carries several different identifiers in the connected generation's namespace, so
    /// which one names it is undecided.
    /// </summary>
    /// <remarks>
    /// A refusal rather than a first-row pick. The rows are matched by the host's own
    /// same-source rule, which treats two spellings of one provider as one source, so an entity can
    /// hold two matching rows carrying two different identifiers. Taking whichever came first would
    /// aim this extension's stored credential at whichever entity the row order happened to name.
    /// </remarks>
    SeveralIdentitiesInThisNamespace,

    /// <summary>The connected generation holds no capability that could honour this.</summary>
    CapabilityAbsentOnThisGeneration,

    /// <summary>The instance offers no quality profile, so no add can be composed.</summary>
    NoQualityProfile,

    /// <summary>The instance offers no library root, so no add can be composed.</summary>
    NoRootFolder,

    /// <summary>The instance answered, and would not do it.</summary>
    InstanceRefused,
}

/// <summary>What one entity's monitoring looks like, as the entity page reads it.</summary>
/// <remarks>
/// Discloses no API key and no part of any response body: only a classified kind and the named
/// values a sentence needs.
/// <para>
/// It carries no count of any sort. A freshly added entity reports a catalogue of zero before any
/// refresh has run, so a count here would be a confident zero this product cannot support.
/// </para>
/// </remarks>
/// <param name="Kind">Which kind of entity this is about.</param>
/// <param name="Generation">The connected generation, or null when none is configured.</param>
/// <param name="Monitored">Whether the connected instance monitors this entity.</param>
/// <param name="Refusal">Why the last thing asked for could not be done, or that it was done.</param>
/// <param name="Capabilities">
/// What the connected generation can do. The browser reads its menu from this rather than from a
/// generation table of its own, so a capability that is absent is refused in one place.
/// </param>
/// <param name="Scope">
/// Which scope the entity is monitored at, or null where the product does not know.
/// <para>
/// Null is DISTINCT from any particular scope: it says the answer this read carried named none, so
/// the browser must mark no scope at all rather than fall back to a default. It carries no date and
/// no part of any response body, only a classified value.
/// </para>
/// </param>
public sealed record EntityMonitoringView(
    WhisparrEntityKind Kind,
    WhisparrGeneration? Generation,
    bool Monitored,
    MonitorRefusalKind Refusal,
    IReadOnlyList<WhisparrCapability> Capabilities,
    MonitorScope? Scope)
{
    /// <summary>A refusal taken before any instance was contacted, with nothing configured.</summary>
    public static EntityMonitoringView NotConfigured(WhisparrEntityKind kind)
        => new(kind, null, false, MonitorRefusalKind.NotConfigured, [], null);

    /// <summary>A refusal naming <paramref name="refusal"/>, with the entity left unmonitored.</summary>
    public static EntityMonitoringView Refused(
        WhisparrEntityKind kind,
        WhisparrGeneration generation,
        IReadOnlyList<WhisparrCapability> capabilities,
        MonitorRefusalKind refusal)
        => new(kind, generation, false, refusal, capabilities, null);

    /// <summary>The entity's state as the instance reports it.</summary>
    /// <remarks>
    /// <paramref name="scope"/> has no default. A defaulted one would let a call site that never
    /// decided the question answer a scope the instance did not report.
    /// </remarks>
    public static EntityMonitoringView State(
        WhisparrEntityKind kind,
        WhisparrGeneration generation,
        IReadOnlyList<WhisparrCapability> capabilities,
        bool monitored,
        MonitorScope? scope)
        => new(kind, generation, monitored, MonitorRefusalKind.None, capabilities, scope);
}
