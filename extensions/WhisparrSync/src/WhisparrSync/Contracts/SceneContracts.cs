using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace WhisparrSync.Contracts;

/// <summary>Why the scene surface cannot answer, or that it can.</summary>
/// <remarks>
/// A wire type, because the sentence a user reads is chosen in the browser from the value answered
/// here. The converter is on the TYPE: an options-level one outranks a type attribute, so a second
/// declaration could drift and win in silence.
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
    /// <remarks>
    /// The scene identity resolution answers this for a library holding several conflicting links
    /// too: which of them an outbound request would name depends on row order, and row order is not
    /// something a reader chose, so it counts as no link.
    /// </remarks>
    NoIdentityInThisNamespace,

    /// <summary>The library holds several conflicting links, each naming a different scene.</summary>
    /// <remarks>
    /// Held apart because the two send a reader to different places: one link is missing and the
    /// other is a link on the scene's own page that does not belong there.
    /// </remarks>
    SeveralIdentitiesInThisNamespace,

    /// <summary>
    /// The connected generation registers no role for the read, so nothing was sent.
    /// </summary>
    /// <remarks>
    /// An absence rather than a decision. The instance was never asked, so a value saying it
    /// declined would name the wrong party and send a reader to their own instance.
    /// </remarks>
    CapabilityAbsentOnThisGeneration,

    /// <summary>The instance was asked and no whole answer arrived.</summary>
    DidNotReachWhisparr,

    /// <summary>The instance answered, and would not do it.</summary>
    InstanceRefused,
}

/// <summary>What the connected instance holds for one scene, as its own tab reads it.</summary>
/// <remarks>
/// The state is carried as the three booleans the browser's own vocabulary derives from, so the
/// words a reader sees are declared in one place and this answer cannot introduce a sixth state.
/// <para>
/// Every named value is nullable, and the surface states a null in that value's own place. The tab
/// draws the same rows for every scene, so an absent value never reads as a failed read.
/// </para>
/// </remarks>
/// <param name="Refusal">Why nothing could be established, or that something was.</param>
/// <param name="Excluded">The scene is on the instance's own exclusion list.</param>
/// <param name="Present">
/// The instance holds an entry for the scene, or null where that could not be established.
/// </param>
/// <param name="Monitored">
/// The instance is looking for the scene, or null where that could not be established.
/// </param>
/// <param name="QualityName">
/// The instance's own name for the quality of the file it holds, or null where it holds no file.
/// </param>
/// <param name="QualityProfileName">
/// The instance's own name for the profile the scene is under, or null where there is none to name.
/// </param>
/// <param name="CutoffName">
/// The quality the profile stops taking better files at, as the profile names it, or null where the
/// profile resolves its own cutoff to nothing.
/// </param>
/// <param name="ProfileReadDidNotComplete">
/// The profile read answered nothing while the scene read succeeded, so the two profile-derived
/// values are absent for a reason that is not about the scene.
/// </param>
public sealed record SceneDetailView(
    SceneRefusalKind Refusal,
    bool Excluded,
    bool? Present,
    bool? Monitored,
    string? QualityName,
    string? QualityProfileName,
    string? CutoffName,
    bool ProfileReadDidNotComplete);
