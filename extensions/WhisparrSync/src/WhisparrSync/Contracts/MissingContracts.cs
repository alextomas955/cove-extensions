using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace WhisparrSync.Contracts;

/// <summary>What one scene's status is, as the catalogue surface reads it.</summary>
/// <remarks>
/// There is no excluded value. An excluded scene has left this set, so a state naming it here could
/// only describe a scene the surface does not carry.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum MissingSceneState
{
    /// <summary>The connected instance holds no entry for the scene.</summary>
    NotAdded,

    /// <summary>The instance holds the scene and monitors it.</summary>
    Monitored,

    /// <summary>The instance holds the scene and does not monitor it.</summary>
    Unmonitored,

    /// <summary>No status could be established, so nothing about the instance is claimed.</summary>
    StatusUnknown,
}

/// <summary>What adding an entity for its catalogue turned out to be.</summary>
/// <remarks>
/// The backend answers with a kind, and the sentence a reader sees is a frontend constant.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum MissingTrackOutcome
{
    /// <summary>The instance now holds the entity and is tracking its catalogue.</summary>
    Added,

    /// <summary>No instance is configured, so nothing was sent.</summary>
    NoInstanceConnected,

    /// <summary>The connected generation addresses no entity of this kind.</summary>
    GenerationCannotTrackThisKind,

    /// <summary>The library names no identifier the instance could be told about.</summary>
    NoIdentifier,

    /// <summary>
    /// The instance declares no quality profile or no library root, so no add could be composed.
    /// </summary>
    NoAddDefaults,

    /// <summary>The add was sent and the instance refused it.</summary>
    Refused,

    /// <summary>Nothing whole arrived, so whether the add took is unknown.</summary>
    NotStarted,
}

/// <summary>What the add answered.</summary>
public sealed record MissingTrackResult(MissingTrackOutcome Outcome);

/// <summary>Why the whole catalogue surface cannot answer, or that it can.</summary>
/// <remarks>
/// Stated once above the grid. Why one card's verb did not take is a separate vocabulary,
/// <see cref="MissingSceneActionRefusal"/>. The backend answers with a kind, and the sentence a user
/// reads is a frontend constant, so nothing a provider or an instance said can reach the copy.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum MissingRefusalKind
{
    /// <summary>Nothing was refused.</summary>
    None,

    /// <summary>No instance is configured, so no status could be read.</summary>
    NoInstanceConnected,

    /// <summary>The host names no metadata source for the connected generation.</summary>
    NoMetadataProviderConfigured,

    /// <summary>
    /// The entity carries no identifier the provider issued, so there is no catalogue to read.
    /// </summary>
    /// <remarks>
    /// One kind whether the library holds no identity row and the name lookup found nothing, or the
    /// lookup found several. Two exact matches leaves the choice to match order, so it counts as no
    /// identifier.
    /// </remarks>
    NoProviderIdForEntity,

    /// <summary>The provider was asked and no whole answer arrived.</summary>
    ProviderUnreachable,

    /// <summary>
    /// The connected instance holds no entry for this entity, so it lists no scenes under it.
    /// </summary>
    /// <remarks>
    /// Not an empty catalogue and not a fault: the instance has simply never been told about the
    /// entity. A reader clears it by adding the entity, which the surface offers.
    /// </remarks>
    EntityNotInWhisparr,

    /// <summary>The instance was asked for the entity's scenes and no whole answer arrived.</summary>
    /// <remarks>Clears on a retry, which the surface offers.</remarks>
    WhisparrCatalogueNotRead,

    /// <summary>
    /// The catalogue was read and the instance was not, so every card carries an unknown status.
    /// </summary>
    /// <remarks>
    /// The catalogue below it is still complete, which is why this is stated beside a grid rather
    /// than in place of one. It clears on a retry.
    /// </remarks>
    WhisparrStatusNotRead,

    /// <summary>
    /// The catalogue was read and the instance keeps no per-scene records, so every card carries an
    /// unknown status.
    /// </summary>
    /// <remarks>
    /// Its own kind because nothing clears it: a retry would read the same absence, so a reader is
    /// offered no retry here.
    /// </remarks>
    WhisparrKeepsNoSceneRecords,
}

/// <summary>Why one card's verb did not take, or that it did.</summary>
/// <remarks>
/// Read beneath the card's own action row, so it is held apart from
/// <see cref="MissingRefusalKind"/>, which is about the grid and is stated once above it.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum MissingSceneActionRefusal
{
    /// <summary>Nothing was refused.</summary>
    None,

    /// <summary>The instance was asked and no answer arrived.</summary>
    DidNotReachWhisparr,

    /// <summary>The instance answered, and would not do it.</summary>
    InstanceRefused,

    /// <summary>
    /// The connected generation registers no role for the verb, so nothing was sent. An absence
    /// rather than a decision: the instance was never asked.
    /// </summary>
    CapabilityAbsentOnThisGeneration,

    /// <summary>The instance offers no quality profile, so no add can be composed.</summary>
    InstanceOffersNoQualityProfile,

    /// <summary>The instance offers no library root, so no add can be composed.</summary>
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
}

/// <summary>One performer named on a catalogue scene.</summary>
public sealed record MissingPerformerChip(string ProviderPerformerId, string Name, string? ImageUrl);

/// <summary>One catalogue scene, with its status already derived.</summary>
/// <remarks>
/// The subtraction of what the library and the instance already hold runs on the server, so this
/// carries a finished card and the provider's own key never reaches the browser.
/// <para>
/// <c>SceneUrl</c> is composed by the source that answered, so the browser holds no address pattern
/// of its own, and is null where the source publishes no address. <c>Performers</c> and <c>Tags</c>
/// are bounded by what one card renders; <c>PerformerCount</c> and <c>TagCount</c> are how many the
/// provider names, however many are shown.
/// </para>
/// </remarks>
public sealed record MissingCard(
    string ProviderSceneId,
    string Title,
    string? ReleaseDate,
    string? CoverUrl,
    string? SceneUrl,
    string? StudioName,
    string? Description,
    IReadOnlyList<MissingPerformerChip> Performers,
    IReadOnlyList<string> Tags,
    int PerformerCount,
    int TagCount,
    MissingSceneState State);

/// <summary>One value a facet menu offers.</summary>
/// <remarks>The value is the opaque string the provider itself issued.</remarks>
public sealed record MissingFacetValue(string Value, string Label);

/// <summary>One facet menu, as the provider filled it.</summary>
/// <remarks>
/// A facet the provider can neither list nor filter by is absent from this list rather than present
/// and empty, so nothing on the toolbar states a capability that does not exist. The key is the
/// opaque one the provider itself issued, and the values are whole-catalogue rather than
/// page-derived. <c>ReportedValueCount</c> is how many values the provider reported, and the menu
/// carries at most one page of them.
/// </remarks>
public sealed record MissingFacetMenu(
    string Key,
    string Label,
    IReadOnlyList<MissingFacetValue> Values,
    int ReportedValueCount);

/// <summary>What a facet-value lookup answered.</summary>
/// <remarks>
/// A lookup that did not answer is held apart from one that matched nothing. Reporting an absence
/// for a value the source holds is the failure this vocabulary exists to keep expressible.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum MissingFacetSearchOutcome
{
    /// <summary>The source answered, with however many values it matched.</summary>
    Matched,

    /// <summary>
    /// The source holds no value list it can search for this facet, so the values already carried
    /// are the whole of what a search can narrow.
    /// </summary>
    NotSearchable,

    /// <summary>
    /// The fragment is shorter than the surface asks the source about, so nothing was sent.
    /// </summary>
    FragmentTooShort,

    /// <summary>The lookup was attempted and no whole answer arrived.</summary>
    NoAnswer,
}

/// <summary>The values of one facet that match what a reader typed.</summary>
/// <remarks>
/// Carries the same rows a menu carries, so a value found here is picked the same way a value the
/// menu was handed is. <c>Values</c> is bounded by what one lookup returns, and
/// <c>ReportedValueCount</c> is how many the source says match.
/// </remarks>
public sealed record MissingFacetSearchView(
    IReadOnlyList<MissingFacetValue> Values,
    int ReportedValueCount,
    MissingFacetSearchOutcome Outcome);

/// <summary>One ordering the provider offers.</summary>
/// <remarks>
/// The value is the opaque string the provider itself issued. One value, never an ordering plus a
/// direction: one provider carries the direction inside each value and the other carries it
/// separately, so a direction model of this product's own would be wrong for one of them.
/// </remarks>
public sealed record MissingSortOption(string Value, string Label);

/// <summary>One page of an entity's catalogue, as the tab reads it.</summary>
/// <remarks>
/// Discloses no provider credential and no part of any response body: classified values and the
/// named ones a sentence needs.
/// <para>
/// <c>CatalogueSize</c> is how many scenes the provider lists for the entity, not the number
/// missing. <c>SizeIsLowerBound</c> says the provider's figure is a floor rather than a count, so
/// the count line renders a trailing plus; the badge carries the plain figure either way, the host
/// taking a number. <c>LastPage</c> is held apart from <c>CatalogueSize</c> because a page count
/// derived from the size would offer pages past a provider ceiling, and one provider clamps a page
/// number past its own and re-serves the last page rather than answering an error.
/// </para>
/// <para>
/// <c>SortInForce</c> is the ordering this page was read under, which is the provider's own where
/// the caller named none, and is null only on a refused page. <c>StatusIsPermanentlyAbsent</c> means
/// no retry can establish a status, because the connected generation keeps no per-scene records.
/// <c>ProviderName</c> is carried on the page rather than held by the surface, because which source
/// answers follows the connected generation.
/// </para>
/// </remarks>
public sealed record MissingPageView(
    IReadOnlyList<MissingCard> Cards,
    int CatalogueSize,
    bool SizeIsLowerBound,
    int Page,
    int PerPage,
    int LastPage,
    int RangeFrom,
    int RangeTo,
    MissingRefusalKind Refusal,
    IReadOnlyList<MissingFacetMenu> Facets,
    IReadOnlyList<MissingSortOption> Sorts,
    string? SortInForce,
    bool StatusWasRead,
    bool StatusIsPermanentlyAbsent,
    string ProviderName);

/// <summary>How large one entity's catalogue is, as the tab badge reads it.</summary>
/// <remarks>
/// The same figure the count line states, read through one call so the two cannot disagree.
/// </remarks>
/// <param name="Count">
/// How many scenes the provider lists, or null where that could not be answered. Null draws no badge
/// at all, the host treating a non-number as no measurement, and the reason is stated inside the tab
/// where there is room for a sentence.
/// </param>
public sealed record MissingCountView(int? Count);

/// <summary>What one card's verb produced.</summary>
public sealed record MissingSceneActionResult(
    MissingSceneState State, MissingSceneActionRefusal Refusal);

/// <summary>What a caller may say when it asks for a whole selection to be marked.</summary>
/// <remarks>
/// The identifiers are bound explicitly, which the single-entity requests in this product do not do.
/// The route names the Cove entity and cannot name which of a page's scenes were ticked, so there is
/// nothing on the server to read them from.
/// </remarks>
/// <summary>Which way a selection of scenes is being marked.</summary>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum MissingBulkVerb
{
    /// <summary>Monitor them, so the instance looks for what it does not hold.</summary>
    Monitor,

    /// <summary>Stop monitoring them. Nothing already downloaded is retracted.</summary>
    Unmonitor,
}

/// <summary>One selection of scenes, and what to do with it.</summary>
/// <remarks>
/// The verb defaults to monitoring, so a body from a browser that names none still reads as the
/// gesture this route has always carried out.
/// </remarks>
public sealed record MissingBulkRequest(
    IReadOnlyList<string> ProviderSceneIds,
    MissingBulkVerb Verb = MissingBulkVerb.Monitor);

/// <summary>What asking for a selection to be marked produced.</summary>
/// <remarks>
/// Exactly one of the two carries the answer: a refusal taken before anything was sent, or the id of
/// the run that was started. It carries no count, because what the run did is a line in the host's
/// job list rather than a number read before the run had offered anything.
/// </remarks>
public sealed record MissingBulkEnqueued(string? JobId, MissingRefusalKind Refusal);
