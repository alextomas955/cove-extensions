using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace WhisparrSync.Contracts;

/// <summary>What one scene's status is, as the catalogue surface reads it.</summary>
/// <remarks>
/// A wire type, because the sentence and the tint a user reads are chosen in the browser from the
/// value answered here. The converter is on the TYPE: an options-level one outranks a type
/// attribute, so a second declaration could drift and win in silence.
/// <para>
/// There is no excluded value. An excluded scene has left this set, so a state naming it here could
/// only describe a scene the surface does not carry.
/// </para>
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

/// <summary>Why the whole catalogue surface cannot answer, or that it can.</summary>
/// <remarks>
/// Why the GRID cannot answer, stated once above it. Why one card's verb did not take is a separate
/// vocabulary, because the two are read in different places and mean different things.
/// <para>
/// The backend answers with a kind. The sentence a user reads is a frontend constant, so nothing a
/// provider or an instance said can reach the copy.
/// </para>
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
    /// lookup found several. Two exact matches leaves the choice to match order, which is not
    /// something a caller or a reader chose, so it counts as no identifier.
    /// </remarks>
    NoProviderIdForEntity,

    /// <summary>The provider was asked and no whole answer arrived.</summary>
    ProviderUnreachable,

    /// <summary>
    /// The catalogue was read and the instance was not, so every card carries an unknown status.
    /// </summary>
    /// <remarks>
    /// The catalogue below it is still complete, which is why this is stated beside a grid rather
    /// than in place of one. It clears on a retry.
    /// </remarks>
    WhisparrStatusNotRead,

    /// <inheritdoc cref="WhisparrStatusNotRead"/>
    /// <remarks>
    /// Its own kind because nothing clears it: the connected generation keeps no per-scene records at
    /// all, so a retry would read the same absence. A reader offered a retry here would be offered a
    /// gesture that cannot change the answer.
    /// </remarks>
    WhisparrKeepsNoSceneRecords,
}

/// <summary>Why one card's verb did not take, or that it did.</summary>
/// <remarks>
/// Read beneath the card's own action row, so it is held apart from
/// <see cref="MissingRefusalKind"/>: that one is about the grid and is stated once above it, and a
/// vocabulary covering both would put a whole-grid sentence under a single card.
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

    /// <summary>The connected generation registers no role for the verb, so nothing was sent.</summary>
    /// <remarks>
    /// An absence rather than a decision. The instance was never asked, so a value saying it
    /// declined would name the wrong party and send a reader to their own instance.
    /// </remarks>
    CapabilityAbsentOnThisGeneration,

    /// <summary>The instance offers no quality profile, so no add can be composed.</summary>
    InstanceOffersNoQualityProfile,

    /// <summary>The instance offers no library root, so no add can be composed.</summary>
    InstanceOffersNoRootFolder,

    /// <summary>The instance holds no entry for the scene, so there was nothing to act on.</summary>
    /// <remarks>
    /// A legitimate answer rather than a failure, and read as one: the instance reported an absence
    /// instead of declining, which sends a reader somewhere different from every other value here.
    /// </remarks>
    WhisparrHasNoEntryForScene,
}

/// <summary>One performer named on a catalogue scene.</summary>
/// <param name="ProviderPerformerId">The identifier the provider issued.</param>
/// <param name="Name">The performer's name as the provider spells it.</param>
/// <param name="ImageUrl">The provider's own picture, or null where it offers none.</param>
public sealed record MissingPerformerChip(string ProviderPerformerId, string Name, string? ImageUrl);

/// <summary>One catalogue scene, with its status already derived.</summary>
/// <remarks>
/// The subtraction of what the library and the instance already hold runs on the server, so this
/// carries a finished card and the provider's own key never reaches the browser.
/// </remarks>
/// <param name="ProviderSceneId">The identifier the provider issued, which the card's verbs name.</param>
/// <param name="Title">The scene title as the provider spells it.</param>
/// <param name="ReleaseDate">The release date the provider carries, or null where it carries none.</param>
/// <param name="CoverUrl">The provider's own cover address, or null where it offers none.</param>
/// <param name="StudioName">The studio the provider names, or null where it names none.</param>
/// <param name="Description">The provider's own description, or null where it carries none.</param>
/// <param name="Performers">The performers the card shows, bounded by what one card renders.</param>
/// <param name="Tags">The tags the card shows, bounded by what one card renders.</param>
/// <param name="PerformerCount">How many performers the provider names, however many are shown.</param>
/// <param name="TagCount">How many tags the provider names, however many are shown.</param>
/// <param name="State">What the connected instance holds for this scene.</param>
public sealed record MissingCard(
    string ProviderSceneId,
    string Title,
    string? ReleaseDate,
    string? CoverUrl,
    string? StudioName,
    string? Description,
    IReadOnlyList<MissingPerformerChip> Performers,
    IReadOnlyList<string> Tags,
    int PerformerCount,
    int TagCount,
    MissingSceneState State);

/// <summary>One value a facet menu offers.</summary>
/// <param name="Value">The opaque string the provider itself issued.</param>
/// <param name="Label">How the value reads.</param>
public sealed record MissingFacetValue(string Value, string Label);

/// <summary>One facet menu, as the provider filled it.</summary>
/// <remarks>
/// A facet the provider can neither list nor filter by is absent from this list rather than present
/// and empty, so nothing on the toolbar states a capability that does not exist.
/// </remarks>
/// <param name="Key">The opaque key the provider itself issued.</param>
/// <param name="Label">How the menu reads.</param>
/// <param name="Values">The values offered, whole-catalogue rather than page-derived.</param>
/// <param name="IsTypeAhead">
/// The menu is longer than one page, so it is filled by asking the provider as the user types rather
/// than from the values here.
/// </param>
public sealed record MissingFacetMenu(
    string Key, string Label, IReadOnlyList<MissingFacetValue> Values, bool IsTypeAhead);

/// <summary>One ordering the provider offers.</summary>
/// <param name="Value">
/// The opaque string the provider itself issued. One value, never an ordering plus a direction: one
/// provider carries the direction inside each value and the other carries it separately, so a
/// direction model of this product's own would be wrong for one of them.
/// </param>
/// <param name="Label">How the option reads.</param>
public sealed record MissingSortOption(string Value, string Label);

/// <summary>One page of an entity's catalogue, as the tab reads it.</summary>
/// <remarks>
/// Discloses no provider credential and no part of any response body: classified values and the
/// named ones a sentence needs.
/// </remarks>
/// <param name="Cards">The scenes this page carries, after what is already held is removed.</param>
/// <param name="CatalogueSize">
/// How many scenes the provider lists for the entity, which is the figure the count line states. It
/// is not the number missing, and the count line says so.
/// </param>
/// <param name="SizeIsLowerBound">
/// The provider's own figure is a floor rather than a count, so the count line renders a trailing
/// plus. The badge carries the plain figure either way, the host taking a number.
/// </param>
/// <param name="Page">The page this view is of.</param>
/// <param name="PerPage">How many scenes a page is read in.</param>
/// <param name="LastPage">
/// The last page the provider will serve. Held apart from <paramref name="CatalogueSize"/> because a
/// page count derived from the size would offer pages past a provider ceiling, and one provider
/// clamps a page number past its own and re-serves the last page rather than answering an error.
/// </param>
/// <param name="RangeFrom">The first position this page covers, as the provider reports it.</param>
/// <param name="RangeTo">The last position this page covers, as the provider reports it.</param>
/// <param name="Refusal">Why the grid cannot answer, or that it can.</param>
/// <param name="Facets">The facet menus the provider filled.</param>
/// <param name="Sorts">The orderings the provider offers.</param>
/// <param name="SortInForce">The ordering this page was read under, or null for the provider's own.</param>
/// <param name="StatusWasRead">Whether a status was established for the scenes on this page.</param>
/// <param name="StatusIsPermanentlyAbsent">
/// No retry can establish a status, because the connected generation keeps no per-scene records.
/// </param>
/// <param name="MonitorAllIsOffered">
/// Whether the whole-entity action is expressible for this entity kind at all.
/// </param>
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
    bool MonitorAllIsOffered);

/// <summary>How large one entity's catalogue is, as the tab badge reads it.</summary>
/// <remarks>
/// The same figure the count line states, read through one call so the two cannot disagree.
/// </remarks>
/// <param name="Count">
/// How many scenes the provider lists, or null where that could not be answered. Null draws no badge
/// at all, the host treating a non-number as no measurement, and the reason is stated inside the tab
/// where there is room for a sentence.
/// </param>
/// <param name="IsLowerBound">
/// The figure is a floor rather than a count. The badge carries it plainly either way; only the
/// count line renders the trailing plus.
/// </param>
public sealed record MissingCountView(int? Count, bool IsLowerBound);

/// <summary>What one card's verb produced.</summary>
/// <param name="State">What the instance holds for the scene after the verb.</param>
/// <param name="Refusal">Why the verb did not take, or that it did.</param>
public sealed record MissingSceneActionResult(
    MissingSceneState State, MissingSceneActionRefusal Refusal);

/// <summary>What a caller may say when it asks for a whole selection to be marked.</summary>
/// <remarks>
/// The identifiers are bound explicitly, which the single-entity requests in this product do not do.
/// The route names the Cove entity and cannot name which of a page's scenes were ticked, so there is
/// nothing on the server to read them from.
/// </remarks>
/// <param name="ProviderSceneIds">The scenes ticked, as the provider issued their identifiers.</param>
public sealed record MissingBulkRequest(IReadOnlyList<string> ProviderSceneIds);

/// <summary>What asking for a selection to be marked produced.</summary>
/// <remarks>
/// Exactly one of the two carries the answer: a refusal taken before anything was sent, or the id of
/// the run that was started. It carries no count, because what the run did is a line in the host's
/// job list rather than a number read before the run had offered anything.
/// </remarks>
/// <param name="JobId">The run the host's job list reports, or null.</param>
/// <param name="Refusal">Why nothing could be started, or that something was.</param>
public sealed record MissingBulkEnqueued(string? JobId, MissingRefusalKind Refusal);
