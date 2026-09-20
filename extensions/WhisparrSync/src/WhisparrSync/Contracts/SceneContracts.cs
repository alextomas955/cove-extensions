using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace WhisparrSync.Contracts;

/// <summary>Why the scene surface cannot answer, or that it can.</summary>
/// <remarks>
/// The backend answers with a kind, and the sentence a user reads is chosen in the browser from it.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum SceneRefusalKind
{
    /// <summary>Nothing was refused.</summary>
    None,

    /// <summary>No instance is configured, so nothing was asked.</summary>
    NoInstanceConnected,

    /// <summary>
    /// The library holds no link for the scene the connected instance could be given.
    /// </summary>
    NoIdentityInThisNamespace,

    /// <summary>The library holds several conflicting links, each naming a different scene.</summary>
    /// <remarks>
    /// Held apart from a missing link because the two send a reader to different places: one link is
    /// missing and the other is a link on the scene's own page that does not belong there.
    /// </remarks>
    SeveralIdentitiesInThisNamespace,

    /// <summary>
    /// The connected generation registers no role for the read, so nothing was sent. An absence
    /// rather than a decision: the instance was never asked.
    /// </summary>
    CapabilityAbsentOnThisGeneration,

    /// <summary>The instance was asked and no whole answer arrived.</summary>
    DidNotReachWhisparr,

    /// <summary>The instance answered, and would not do it.</summary>
    InstanceRefused,

    /// <summary>The instance offers no quality profile, so no add can be composed.</summary>
    /// <remarks>
    /// A stop taken before anything is sent. This generation accepts a profile id of zero, echoes it
    /// back, and the scene then monitors and can never acquire anything.
    /// </remarks>
    InstanceOffersNoQualityProfile,

    /// <summary>The instance offers no library root, so no add can be composed.</summary>
    /// <remarks>
    /// A stop taken before anything is sent. A fresh instance is exactly this case, and its add
    /// answers a conflict carrying a full stack trace.
    /// </remarks>
    InstanceOffersNoRootFolder,

    /// <summary>
    /// The library folder this scene's own files sit in is one the instance has agreed no spelling
    /// for, so no add can be composed.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="InstanceOffersNoRootFolder"/>, which is the instance offering no
    /// root at all. Here what is unsettled is which of those roots holds this scene's files, which
    /// is a folder mapping rather than a Whisparr setting.
    /// </remarks>
    NoAgreedRootForThisEntity,

    /// <summary>The instance holds no entry for the scene, so there was nothing to act on.</summary>
    /// <remarks>
    /// A legitimate answer rather than a failure: the instance reported an absence instead of
    /// declining, which sends a reader somewhere different from every other value here.
    /// </remarks>
    WhisparrHasNoEntryForScene,

    /// <summary>The instance already holds an entry for the scene, so an add would add nothing.</summary>
    /// <remarks>
    /// Read off the instance's own row before the add is composed, so a second entry is never
    /// created. Not a failure: the state the reader wanted is the state the instance is already in.
    /// </remarks>
    WhisparrAlreadyHoldsThisScene,

    /// <summary>The instance holds the scene and is not looking for it, so nothing was sent.</summary>
    /// <remarks>
    /// Held apart from an absent entry: a search on a scene the instance is not monitoring would
    /// find nothing whatever the indexers hold.
    /// </remarks>
    WhisparrIsNotMonitoringThisScene,
}

/// <summary>What one selection of scenes is asked to do.</summary>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum SceneBatchVerb
{
    /// <summary>Add each scene the instance holds no entry for.</summary>
    Add,

    /// <summary>Ask the instance to want each scene it holds.</summary>
    Monitor,

    /// <summary>Ask the instance to stop wanting each scene it holds.</summary>
    Unmonitor,

    /// <summary>Ask the instance to look for each scene it holds and monitors.</summary>
    Search,

    /// <summary>Put each scene on the instance's own exclusion list.</summary>
    Exclude,
}

/// <summary>What a caller may say when it asks for a selection of scenes to be acted on.</summary>
/// <remarks>
/// Every member is nullable, so a body naming none is a refusal this product writes with a code the
/// browser can read rather than a bind failure whose shape it does not choose. <c>EntityType</c>
/// arrives in the spelling the host's bar passed. The verb decides which bound applies, and
/// <c>CoveIds</c> arrives with repeats and all.
/// </remarks>
public sealed record SceneBatchRequest(
    string? EntityType, SceneBatchVerb? Verb, int[]? CoveIds);

/// <summary>What one of the scene tab's own verbs produced.</summary>
/// <remarks>
/// It carries no state. The browser re-reads the scene's facts after every verb, so what a reader
/// sees is read off the instance rather than painted from what the browser asked for.
/// <para>
/// <c>SearchIsWithWhisparr</c> means the instance holds the search command, read back off it by the
/// command's own id. It says nothing about a download: no answer here reports a file, a release or
/// a queue.
/// </para>
/// </remarks>
public sealed record SceneActionResult(
    SceneRefusalKind Refusal, bool SearchIsWithWhisparr);

/// <summary>What the connected instance holds for one scene, as its own tab reads it.</summary>
/// <remarks>
/// The state is carried as the three booleans the browser's own vocabulary derives from, so the
/// words a reader sees are declared in one place and this answer cannot introduce a sixth state.
/// <para>
/// Every named value is nullable, and the surface states a null in that value's own place. The tab
/// draws the same rows for every scene, so an absent value never reads as a failed read.
/// <c>CutoffName</c> is the quality the profile stops taking better files at, and is null where the
/// profile resolves its own cutoff to nothing. <c>ProfileReadDidNotComplete</c> says the profile
/// read answered nothing while the scene read succeeded, so the two profile-derived values are
/// absent for a reason that is not about the scene.
/// </para>
/// </remarks>
public sealed record SceneDetailView(
    SceneRefusalKind Refusal,
    bool Excluded,
    bool? Present,
    bool? Monitored,
    string? QualityName,
    string? QualityProfileName,
    string? CutoffName,
    bool ProfileReadDidNotComplete);
