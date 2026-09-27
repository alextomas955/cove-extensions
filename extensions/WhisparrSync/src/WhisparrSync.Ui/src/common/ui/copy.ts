/** The product's specified sentences. */

/** The library toolbar control's own name while the card badges are hidden. */
export const SHOW_WHISPARR_STATUS = "Show Whisparr status";

/** The same control's name once the badges are on. */
export const HIDE_WHISPARR_STATUS = "Hide Whisparr status";

/** The row of counts under a list toolbar, named for what it counts. */
export const WHISPARR_STATUS_ROW = "Whisparr";

/** What the row's counts are over, stated on the row itself. */
export const LIBRARY_COUNTS_ARE_FOR_THIS_PAGE =
  "These counts are for the cards on this page, not for the whole library.";

/** The row while its cards are still being read. */
export const CHECKING_WHISPARR = "Checking Whisparr…";

/**
 * Said beside the counts while cards are still being read, because a page is answered a batch at a
 * time and a running subtotal reads exactly like a finished one.
 */
export const STILL_COUNTING = "still counting";

/** Nothing could be reached, said once for the page on the control that asked. */
export const WHISPARR_STATUS_COULD_NOT_BE_READ =
  "Cove could not reach Whisparr, so no card can show a status. That is not the same as Whisparr holding nothing.";

/** Nothing is connected, so nothing was asked, said once for the page on the control that asked. */
export const NO_WHISPARR_CONNECTED =
  "No Whisparr is connected, so no card can show a status. Nothing was asked of Whisparr.";

/** The connected Whisparr keeps no record of the cards on this page, so it was not asked. */
export const WHISPARR_KEEPS_NO_RECORD_OF_THESE =
  "The connected Whisparr keeps no record of these, so no card can show a status. It was not asked.";

/** The status request itself never answered, said once for the page on the control that made it. */
export const THE_STATUS_READ_DID_NOT_COMPLETE =
  "Cove could not complete the status read, so no card can show a status. Whisparr may not have been asked at all.";

/** The badges are on where the display mode draws none, said on the control that turned them on. */
export const NO_PLACE_FOR_A_CARD_STATUS_HERE =
  "This display mode has no place for a per-card status. Switch to the Grid display mode to see it.";

/**
 * The name every menu row carries, which is not the name the scene tab's control of the same verb
 * carries.
 */
export const MENU_ADD = "Add";

/** Sits beside {@link MENU_UNMONITOR}, so the pair reads as one axis at a glance. */
export const MENU_MONITOR = "Monitor";

export const MENU_UNMONITOR = "Unmonitor";

export const MENU_EXCLUDE = "Exclude";

/** The scene tab's monitor control, while the instance is not monitoring the scene. */
export const MONITOR_IN_WHISPARR = "Monitor in Whisparr";

/** The entity control's own name while the entity is not monitored in Whisparr. */
export const WHISPARR_NOT_MONITORED = "Whisparr, not monitored";

/** The same control's name once the connected instance monitors the entity. */
export const WHISPARR_MONITORED = "Whisparr, monitored";

/** The entity's monitored state could not be read. */
export const MONITORING_COULD_NOT_BE_READ =
  "Cove could not read what Whisparr monitors for this. That is not the same as Whisparr monitoring nothing.";

/** An action that never reached the instance. */
export const ACTION_DID_NOT_REACH_WHISPARR =
  "Cove could not carry that out. Nothing here was changed; try again shortly.";

/** An action this build of the extension does not carry out. */
export const ACTION_ABSENT_IN_THIS_VERSION =
  "This version of Whisparr Sync does not carry out this action.";

/** The narrower monitor scope. */
export const SCOPE_FUTURE_SCENES = "New releases only";

/** The wider monitor scope, named for what it does with what Whisparr already lists. */
export const SCOPE_ALL_SCENES = "All scenes";

/** What the wider scope costs, stated where the scope is chosen rather than after it is taken. */
export const ALL_SCENES_MARKS_THE_BACK_CATALOGUE =
  "Monitoring all scenes monitors every scene Whisparr already lists for this entity, which spends indexer traffic and disk.";

/** That the wider scope is a one-way door, stated where the scope is chosen. */
export const ALL_SCENES_IS_NOT_UNDONE_BY_A_LATER_SCOPE_CHANGE =
  "Narrowing the scope back to new releases only does not undo this: a scene already monitored stays monitored.";

/** What the search costs, stated where it is chosen rather than after it is taken. */
export const SEARCH_ALL_MONITORED_SPENDS_TRAFFIC_AND_DISK =
  "Whisparr looks for everything these entities monitor and do not hold, and takes in what it finds. That spends indexer traffic and disk.";

/** Why nothing was linked. Names the setting, because turning it on is what changes the answer. */
export const REFLECT_OWNED_SKIPPED =
  "Skipped: with Whisparr's hard-link setting off each file would be copied rather than linked, and would use disk twice.";

/** Why nothing was linked when the setting itself could not be read. */
export const REFLECT_OWNED_SKIPPED_SETTING_UNREADABLE =
  "Skipped: Cove could not read Whisparr's hard-link setting, so it could not tell whether linking these would cost extra disk.";

/** Why nothing was linked. Names where to turn renaming off, because that is what changes the answer. */
export const REFLECT_OWNED_SKIPPED_RENAMING_ON =
  "Skipped: Whisparr is set to rename files, and linking works only while it leaves a file where it found it. Turn renaming off under its Settings, Media Management.";

/** Why nothing was linked when the rename setting itself could not be read. */
export const REFLECT_OWNED_SKIPPED_RENAME_SETTING_UNREADABLE =
  "Skipped: Cove could not read Whisparr's rename setting, so it could not tell whether Whisparr would leave these files alone.";

/**
 * The same scene tab control once the instance monitors the scene. Named for what it does, not for
 * the flag it writes.
 */
export const STOP_MONITORING_IN_WHISPARR = "Stop monitoring in Whisparr";

/** Nothing could be offered, because what the connected Whisparr can do was not read. */
export const BULK_ACTIONS_COULD_NOT_BE_OFFERED =
  "Cove could not read what the connected Whisparr can do, so it offered nothing. Nothing was changed; try again shortly.";

/**
 * Why a selection larger than the bound did nothing.
 *
 * @param bound the count the route refused above, read off its own refusal
 */
export function bulkSelectionIsOverTheBoundSentence(bound: number): string {
  return `Cove acts on at most ${String(bound)} entities in one run, and you selected more. Nothing was changed; select fewer and repeat over the rest.`;
}

/** Any other refusal of the whole gesture. */
export const RUN_WAS_NOT_STARTED =
  "Cove could not start this run. Nothing was changed; try again shortly.";

/** The overlay's way out, on the choice it presents. */
export const BULK_CANCEL = "Cancel";

/** The overlay's way out when there is nothing to choose between. */
export const BULK_CLOSE = "Close";

/** The three secondary actions, named as the product names them everywhere it offers them. */
/** Re-checks the catalogue against the provider and the connected instance. */
export const ACTION_REFRESH = "Refresh";

export const ACTION_ADD_ALL_MISSING = "Add all missing";
export const ACTION_REFLECT_OWNED = "Reflect owned";
export const ACTION_SEARCH_ALL_MONITORED = "Search all monitored";

/** Why a control is unavailable while the last thing asked for is still on its way. */
export const WAITING_FOR_WHISPARR = "Waiting for Whisparr to answer the last thing you asked for.";

/** Nothing is connected, said on a page that is not the settings page. */
export const NO_INSTANCE_CONNECTED =
  "No Whisparr instance is connected. Connect one on the Whisparr Sync settings page.";

/** The entity carries no identifier the connected instance could be given. */
export const NO_IDENTITY_IN_THIS_NAMESPACE =
  "Cove holds no link for this entity that the connected Whisparr can identify it by.";

/**
 * The entity carries several links the connected instance would read as the same source, naming
 * different entities.
 */
export const SEVERAL_IDENTITIES_IN_THIS_NAMESPACE =
  "Cove holds conflicting links for this entity, so Whisparr cannot be told which one it is. Remove the links that do not belong, on the entity's page in Cove.";

/** The instance offers no quality profile, so nothing could be composed to send. */
export const INSTANCE_OFFERS_NO_QUALITY_PROFILE =
  "Whisparr offers no quality profile, so nothing was sent. Add one in Whisparr and try again.";

/** The instance offers no library root, so nothing could be composed to send. */
export const INSTANCE_OFFERS_NO_ROOT_FOLDER =
  "Whisparr offers no root folder, so nothing was sent. Add one in Whisparr and try again.";

/** The folder this entity's own files sit in has no agreed Whisparr spelling. */
export const NO_AGREED_ROOT_FOR_THIS_ENTITY =
  "Cove and Whisparr have not agreed where this entity's files are, so nothing was sent. Settle that folder on the settings page.";

/** The instance answered and declined. */
export const INSTANCE_REFUSED = "Whisparr would not do this. Nothing here was changed.";

/** The instance answered, and the answer was past what this extension reads at once. */
export const INSTANCE_ANSWER_WAS_TOO_LARGE_TO_READ =
  "Whisparr answered correctly, with more than this extension reads at once. Reload the page for its current state.";

/** The instance answered, and holds no such entry. */
export const INSTANCE_HOLDS_NO_SUCH_ENTRY =
  "Whisparr no longer holds this entry, so there was nothing to act on. Reload the page for its current state.";

/** The change was accepted, and the read taken straight after it does not report it. */
export const INSTANCE_DID_NOT_REPORT_THE_CHANGE =
  "Whisparr accepted the change but does not report it. Reload the page for its current state.";

/** The version-gap sentence, for a capability the connected generation does not have. */
export const CAP_UNAVAILABLE_ON_THIS_GENERATION = "Currently available on Whisparr v3 (Eros)";

/**
 * A search asked for on an entity Whisparr does not hold. A true statement, not a failure, so it
 * renders differently from a failed request.
 */
export const SEARCH_WITH_NO_ENTRY =
  "Whisparr has no entry for this scene yet, so there is nothing to search for - monitor it first.";

/** The product's own name, drawn beside the state chip in the scene tab's header. */
export const SCENE_HEADER_WHISPARR = "Whisparr";

/**
 * The accessible name of the product's mark, which carries no text of its own.
 *
 * Its own constant rather than one of the two above. Those name text the reader sees on other
 * surfaces, and an edit to either of them must not rename the mark with it.
 */
export const WHISPARR_LOGO_NAME = "Whisparr";

/** The label on the scene tab's quality row, which names the file the instance holds. */
export const SCENE_FACT_QUALITY = "Quality";

/** The label on the scene tab's profile row. */
export const SCENE_FACT_PROFILE = "Quality profile";

/** The label on the scene tab's cutoff row, which the profile above it resolves. */
export const SCENE_FACT_CUTOFF = "Cutoff";

/** The scene tab's add control. */
export const SCENE_ADD = "Add to Whisparr";

/** The scene tab's search control, named as both surfaces name it. */
export const SCENE_SEARCH = "Search now";

/** The excluding half of the tab's one exclusion control. */
export const SCENE_EXCLUDE = "Exclude from Whisparr";

/** The removing half of the same control, which is what the reader sees once it is excluded. */
export const SCENE_REMOVE_EXCLUSION = "Remove exclusion";

/** A search asked for on a scene the instance holds no entry for, on the scene's own tab. */
export const SCENE_SEARCH_NEEDS_AN_ENTRY =
  "Whisparr has no entry for this scene, so there is nothing to search for. Add it first.";

/** The monitor control's own no-entry sentence, which names monitoring rather than searching. */
export const SCENE_MONITOR_NEEDS_AN_ENTRY =
  "Whisparr has no entry for this scene, so there is nothing to monitor. Add it first.";

/** A search asked for on a scene the instance holds and is not monitoring. */
export const SCENE_SEARCH_NEEDS_MONITORING =
  "Whisparr only looks for a scene it is monitoring, so nothing was sent. Monitor it first.";

/** The add control's reason once the instance holds the scene. */
export const SCENE_IS_ALREADY_IN_WHISPARR =
  "Whisparr already holds this scene, so there is nothing to add.";

/** Why the other three controls are unavailable on an excluded scene. */
export const SCENE_IS_ON_THE_EXCLUSION_LIST =
  "This scene is on Whisparr's exclusion list, so Whisparr will not act on it. Remove the exclusion first.";

/** What confirms a search, read back off the instance by the command's own id. */
export const SCENE_SEARCH_IS_WITH_WHISPARR =
  "Whisparr has the search. What it finds arrives the same way every other import does.";

/**
 * Why a selection larger than the search row's own bound did nothing.
 *
 * @param bound the count the route refused above, read off its own refusal
 */
export function batchSearchIsOverTheBoundSentence(bound: number): string {
  return `A search runs against every indexer Whisparr has, so Cove searches at most ${String(bound)} scenes in one run, and you selected more. Nothing was sent; select fewer and repeat over the rest.`;
}

/** What a `{provider}` slot reads as before any page has answered. */
export const THE_METADATA_SOURCE = "your metadata source";

/** A missing-check whose provider did not answer. */
export const PROVIDER_UNREACHABLE =
  "Couldn't reach {provider} to check what's missing for {entity}. This isn't the same as owning everything - try again shortly.";

/** A missing-check that succeeded and found nothing missing. Only for a check that did succeed. */
export const NOTHING_MISSING = "You own every scene {provider} lists for {entity}.";

/** A page whose scenes were all owned, in a catalogue that still holds others. */
export const EVERY_SCENE_ON_THIS_PAGE_IS_OWNED =
  "You own every scene on this page. Later pages hold the ones you do not.";

/** A parent studio read without its sub-studios, which the provider attributes every scene to. */
export const NO_SCENES_WITHOUT_SUB_STUDIOS =
  "{provider} lists this studio's scenes under its sub-studios. Turn on “Include sub-studio content” above to see them.";

/** Cove names no metadata source, so there is nothing to read a catalogue from. */
export const NO_METADATA_PROVIDER_CONFIGURED =
  "Set up a {provider} metadata source in Cove (Settings → Scraping → Metadata servers) to discover {entity}'s catalogue.";

/** The entity carries no identifier the provider issued, and its name matched nothing exactly. */
export const NO_PROVIDER_ID_FOR_ENTITY =
  "No {provider} id for {entity}, so Whisparr cannot be told which entity this is. Identify it in Cove first.";

/**
 * Whisparr holds no entry for the entity, so it lists no scenes under it. The surface offers the
 * add that makes the list exist.
 */
export const ENTITY_NOT_IN_WHISPARR =
  "Whisparr does not have {entity} yet, so it lists no scenes to compare against your library.";

/** What the add offered beside that sentence does, stated before it is pressed. */
export const ADD_TO_WHISPARR_TRACKS_ONLY =
  "Adding it lets Whisparr list every scene it knows of. Nothing is monitored and nothing is downloaded until you say so.";

/** The control that adds the entity for its catalogue alone. */
export const ADD_TO_WHISPARR = "Add to Whisparr";

/** The same control while the add is in flight. */
export const ADDING_TO_WHISPARR = "Adding...";

/** The instance was asked for the entity's scenes and nothing whole arrived. */
export const WHISPARR_CATALOGUE_NOT_READ =
  "Cove could not read what Whisparr lists for {entity}. That is not the same as Whisparr listing nothing - try again shortly.";

/** The connected generation addresses no entity of this kind, so it can track none. */
export const WHISPARR_CANNOT_TRACK_THIS_KIND =
  "The connected Whisparr does not hold entries of this kind, so it cannot list scenes for {entity}.";

/** The instance declares no profile or no root, so no add could be composed. */
export const WHISPARR_HOLDS_NO_ADD_DEFAULTS =
  "Whisparr declares no quality profile or no library root, so nothing could be added. Set both in Whisparr first.";

/** The add was sent and did not take, said on the control that sent it. */
export const ADD_TO_WHISPARR_DID_NOT_TAKE =
  "Whisparr did not add {entity}. Nothing was changed; try again shortly.";

/** A title search over the whole catalogue that matched nothing. Renders with a way to clear it. */
export const NO_TITLES_MATCH = "No titles match that search.";

/** A filtered catalogue that matched nothing. */
export const NO_SCENES_MATCH_THESE_FILTERS =
  "No scenes match these filters. Clear them to see the whole catalogue.";

/** The catalogue was read and Whisparr was not. */
export const WHISPARR_STATUS_NOT_READ =
  "Cove could not reach Whisparr, so it could not read a status for these. The catalogue below is still complete.";

/** The connected Whisparr holds no per-scene records at all. */
export const WHISPARR_KEEPS_NO_SCENE_RECORDS =
  "The connected Whisparr keeps no per-scene records, so Cove cannot read a status for these. The catalogue below is still complete.";

/**
 * What a surface says while a run this browser started is still working through something.
 *
 * Names no outcome: the run is still going, and what the instance ends up holding is read when it
 * stops.
 */
export const WORKING_IN_WHISPARR = "Working";

/**
 * The five names an entity's Whisparr state goes by, wherever one appears.
 *
 * Reusing one for a different meaning in another view changes what the product means by a state.
 */
export const STATE_MONITORED = "Monitored";

/** @see STATE_MONITORED */
export const STATE_UNMONITORED = "Unmonitored";

/** @see STATE_MONITORED */
export const STATE_NOT_ADDED = "Not added";

/** @see STATE_MONITORED */
export const STATE_EXCLUDED = "Excluded";

/** @see STATE_MONITORED */
export const STATE_STATUS_UNKNOWN = "Unknown";

/** The marker saying the instance holds a file, which is drawn beside a state and never instead. */
export const MARKER_IN_LIBRARY = "In library";

/** The marker for an entity the instance could not be told which one it is, so was never asked. */
export const MARKER_NOT_LINKED = "Not linked";

/** Why the card was never asked about, for the chip's own title. */
export const NOT_LINKED_REASON =
  "No id Whisparr could name this by, so it was never asked. Identify it in Cove first.";

/** The catalogue tab's own name, drawn at the left of its toolbar. */
export const MISSING_TAB_HEADING = "Missing";

/** The name of the card control that ticks one scene, which carries no text of its own. */
export const MISSING_SELECT_SCENE = "Select scene";

/** The same control once the scene is ticked. */
export const MISSING_DESELECT_SCENE = "Deselect scene";

/** The control the empty state offers that puts the whole catalogue back. */
export const MISSING_CLEAR_FILTERS = "Clear filters";

/** The control it offers beside that one, which drops the title search alone. */
export const MISSING_CLEAR_SEARCH = "Clear search";

/** The accessible name of the page control under the grid, which announces what it pages. */
export const MISSING_PAGES = "Missing scenes pages";

/** The placeholder in the toolbar's title search. */
export const MISSING_SEARCH_PLACEHOLDER = "Search titles";

/** The ordering menu's own name. */
export const MISSING_SORT_MENU = "Sort";

/** The control that marks everything the source lists for the entity. */
export const MISSING_MONITOR_ALL = "Monitor all";

/** The selection bar's three gestures, worded as Cove's own list selection bar words them. */
export const SELECT_ALL = "Select all";

/** @see SELECT_ALL */
export const DESELECT_ALL = "Deselect all";

/** @see SELECT_ALL */
export const INVERT_SELECTION = "Invert";

/**
 * The two verbs the selection bar offers over the ticked scenes.
 *
 * Declared apart from the menu rows of the same verb, so renaming a row leaves the bar alone.
 */
export const SELECTION_MONITOR = "Monitor";

/** @see SELECTION_MONITOR */
export const SELECTION_UNMONITOR = "Unmonitor";

/**
 * What a card's footer counts, or null where it counts nothing.
 *
 * A half that counts nothing is left out rather than rendered as a zero, which would read as a
 * measurement of the scene.
 */
export function cardCountsLine(performerCount: number, tagCount: number): string | null {
  const parts: string[] = [];
  if (performerCount > 0) {
    parts.push(`${String(performerCount)} ${performerCount === 1 ? "performer" : "performers"}`);
  }
  if (tagCount > 0) {
    parts.push(`${String(tagCount)} ${tagCount === 1 ? "tag" : "tags"}`);
  }
  return parts.length === 0 ? null : parts.join(" · ");
}

/** The name of the card control that monitors one scene. */
export function monitorSceneName(title: string): string {
  return `Monitor ${title} in Whisparr`;
}

/** The name of the card control that asks Whisparr to look for one scene, under the same rule. */
export function searchSceneName(title: string): string {
  return `Search Whisparr for ${title}`;
}

/** The name of the card link that opens one scene where its source shows it. */
export function openSceneName(title: string): string {
  return `Open ${title} at your metadata source`;
}

/** A control's name while its own request is unanswered. */
export function nameWhileWaiting(name: string): string {
  return `${name}. ${WAITING_FOR_WHISPARR}`;
}

/** What a facet control reads while nothing is picked in it. */
export function facetCoversEverything(menuLabel: string): string {
  return `All ${menuLabel.toLowerCase()}`;
}

/** The range a page covers, out of the whole catalogue. */
export function countLine(from: number, to: number, total: number, atCeiling: boolean): string {
  return `${String(from)}–${String(to)} of ${String(total)}${atCeiling ? "+" : ""}`;
}

/** The placeholder in a facet menu's search box. */
export const FACET_MENU_SEARCH = "Search values";

/** What a facet menu reads when nothing it holds matches, its values having not been looked up. */
export const FACET_MENU_NO_MATCHES = "No values in this menu match.";

/** What a facet menu reads while the source is being asked for the values that match. */
export const FACET_VALUES_ASKING = "Looking for matching values.";

/** What a facet menu reads when the source answered and matched nothing. */
export const FACET_VALUES_NONE_MATCH = "The metadata source lists no value matching this.";

/** What a facet menu reads when the lookup did not answer. */
export const FACET_VALUES_NOT_READ =
  "The values could not be read. That is not the same as the source listing none that match.";

/** The accessible name of a facet menu's search box, which the menu's own name leads. */
export function facetMenuSearchLabel(menuLabel: string): string {
  return `Search ${menuLabel.toLowerCase()} values`;
}

/** How many scenes are ticked. */
export function selectionCount(n: number): string {
  return n === 1 ? "1 selected" : `${String(n)} selected`;
}

/** What heads the selection popover, and the name the panel announces. */
export function selectionMenuHeader(n: number): string {
  return `Whisparr · ${selectionCount(n)}`;
}

/** How many entities a choice made over a selection covers. */
export function entitiesCovered(n: number): string {
  return n === 1 ? "This covers 1 entity." : `This covers ${String(n)} entities.`;
}

/**
 * What the confirmation in front of the wider scope states.
 *
 * @param count how many entities the choice covers
 * @param oneWayDoor whether a later scope change leaves what the wider scope already monitored
 */
export function allScenesConfirmation(count: number, oneWayDoor: boolean): string {
  return [
    entitiesCovered(count),
    ALL_SCENES_MARKS_THE_BACK_CATALOGUE,
    ...(oneWayDoor ? [ALL_SCENES_IS_NOT_UNDONE_BY_A_LATER_SCOPE_CHANGE] : []),
  ].join(" ");
}

/** What marking a catalogue does not do, stated where the whole catalogue is about to be marked. */
export const MONITOR_ALL_DOWNLOADS_NOTHING_BY_ITSELF =
  "Monitoring a scene downloads nothing by itself.";

/**
 * What the confirmation in front of the whole catalogue states.
 *
 * @param count how many scenes the source lists for the entity
 * @param provider the metadata source the catalogue was read from, as a sentence names it
 */
export function monitorAllConfirmation(count: number, provider: string): string {
  const catalogue =
    count === 1
      ? `the 1 scene ${provider} lists here`
      : `all ${String(count)} scenes ${provider} lists here`;

  return `This covers ${catalogue}, minus the ones you already have. ${MONITOR_ALL_DOWNLOADS_NOTHING_BY_ITSELF}`;
}

/**
 * What the confirmation in front of the search states.
 *
 * @param count how many entities the choice covers
 */
export function searchAllMonitoredConfirmation(count: number): string {
  return [entitiesCovered(count), SEARCH_ALL_MONITORED_SPENDS_TRAFFIC_AND_DISK].join(" ");
}

/** Imports Cove recorded but can no longer read. Self-clears on a success. */
export const IMPORTS_UNREADABLE = "Sync problem - Cove can't find imported files";

/** The read behind the block failed, so whether any import was refused is not known. */
export const IMPORT_REPORT_UNREADABLE =
  "Cove could not read what happened to the files Whisparr imported.";

/** A refresh that failed over content already on screen. */
export const READ_IS_STALE =
  "Cove couldn't check this just now. These are the last values it read.";

/** No Cove library folder holds the reported file at all. */
export const IMPORT_CAUSE_NOT_FOUND = "No Cove library folder holds this file.";

/** The reported name is under more than one library folder, so none was chosen. */
export const IMPORT_CAUSE_AMBIGUOUS =
  "This name is under more than one of your library folders, so Cove did not choose between them.";

/** The file was found where it was reported and Cove's own import would not take it. */
export const IMPORT_CAUSE_UNREADABLE = "Cove found this file and would not take it in.";

/** A download that landed in the folders Cove keeps for Whisparr and could not be moved on. */
export const IMPORT_CAUSE_NOT_PLACED =
  "Cove could not give this downloaded file a place in your library, so it was left where Whisparr put it.";

/** One Whisparr root folder's outstanding refusals. */
export function importRefusalsUnderRootSentence(root: string, count: number): string {
  return `${String(count)} ${count === 1 ? "file" : "files"} under ${root} ${count === 1 ? "has" : "have"} not reached your library since an import from it last worked.`;
}

/** Refusals Whisparr reported under none of its own root folders. */
export function importRefusalsWithNoReportedRootSentence(count: number): string {
  return `${String(count)} ${count === 1 ? "file" : "files"} ${count === 1 ? "has" : "have"} not reached your library, and Whisparr reported ${count === 1 ? "it" : "them"} under none of its own root folders.`;
}

/** Files Whisparr reported that Cove's catch-up could not take, and has already moved past. */
export function importsPassedOverSentence(count: number, when: string | null): string {
  const files = count === 1 ? "file" : "files";
  const them = count === 1 ? "it" : "them";
  const passedOver = `${String(count)} ${files} Whisparr reported could not be taken in, and Cove's regular catch-up has moved past ${them}, so it will not try ${them} again.`;
  return when === null ? passedOver : `${passedOver} Most recently ${when}.`;
}

/** The section holding every folder whose Whisparr path is not Cove's own to work out. */
export const FOLDER_AGREEMENT_TITLE = "Where Whisparr holds your folders";

/** What the section is for, and which folders reach it. */
export const FOLDER_AGREEMENT_DESCRIPTION =
  "Whisparr reaches your files at paths of its own, and Cove works each one out by asking it what it holds. Listed here are the folders it could not settle, and the ones you set a path for.";

/** The read behind the section failed, so which folders are listed is not known. */
export const FOLDER_AGREEMENT_UNREADABLE = "Cove could not read where Whisparr holds your folders.";

/** What the reader types: where the connected instance holds this one folder. */
export const FOLDER_AGREEMENT_PATH = "Where Whisparr holds this folder";

/** What the field takes, and what leaving it blank does. */
export const FOLDER_AGREEMENT_PATH_HELPER = "Leave it blank to let Cove work the path out again.";

/** The control that states the path. */
export const FOLDER_AGREEMENT_SAVE = "Save";

/** The control that opens the path field under a folder with nothing outstanding. */
export const FOLDER_AGREEMENT_CHANGE = "Change";

/** The control on a line where withdrawing the path in force is the only thing that can be done. */
export const FOLDER_AGREEMENT_WITHDRAW = "Withdraw this path";

/** Why nothing under one prompt can act while its own save is in flight. */
export const FOLDER_AGREEMENT_SAVE_IS_RUNNING = "Cove is checking this path with Whisparr.";

/** The stated path is working, so the folder is listed to be reviewed rather than acted on. */
export const FOLDER_AGREEMENT_SETTLED = "Settled";

/** Cove could not work the path out, and a stated one would settle it. */
export const FOLDER_AGREEMENT_NEEDS_A_PATH = "Needs a path";

/** Nothing under the folder can be asked about, so no stated path would change anything. */
export const FOLDER_AGREEMENT_NOTHING_TO_SETTLE = "Nothing to settle";

/** Stands where a folder's Whisparr path would be, so a row always reads as a pair. */
export const FOLDER_AGREEMENT_NO_PATH_YET = "no path yet";

/** No candidate path held a file of the size the library holds. */
export const FOLDER_NOTHING_RESOLVED =
  "Whisparr holds no file of the right size at any path Cove asked it about.";

/** Several candidates held one, so which of them the folder means was not settled. */
export const FOLDER_MORE_THAN_ONE_RESOLVED =
  "More than one of the paths Cove asked about holds the file, and the two cannot be told apart. Stating the path settles it.";

/** The instance declares no folder of its own to rebuild a path under. */
export const FOLDER_INSTANCE_DECLARES_NO_ROOT =
  "Whisparr declares no folder of its own, so Cove had nothing to build a path from.";

/** Cove holds no file under this folder to establish the agreement from. Nothing is misconfigured. */
export const FOLDER_NO_FILE_TO_PROBE_WITH =
  "This folder holds no file for Cove to ask Whisparr about yet, so there is nothing to settle here.";

/** The instance was asked and its answer could not be read, which is not an answer of no. */
export const FOLDER_PROBE_COULD_NOT_BE_READ =
  "Cove asked Whisparr about this folder and could not read the answer, which is not the same as Whisparr holding nothing.";

/** The connected instance offers no way to ask what it holds. */
export const FOLDER_INSTANCE_CANNOT_BE_ASKED =
  "The Whisparr Cove is connected to offers it no way to ask what it holds.";

/** The folder sits under none of the host's own library folders. */
export const FOLDER_UNDER_NO_LIBRARY_ROOT =
  "This folder is under none of Cove's own library folders.";

/** The probe run against a stated path resolved, so the path is now in use. */
export const FOLDER_SAVE_STORED =
  "Whisparr holds this folder there. Cove uses that path from the next run.";

/** A blank path removed the stated one, so Cove works the path out for itself again. */
export const FOLDER_SAVE_REMOVED = "Cove works Whisparr's path out for itself again.";

/** The path named is none of Cove's own library folders, so it could never be asked about. */
export const FOLDER_SAVE_NOT_A_LIBRARY_ROOT =
  "That is none of Cove's own library folders, so nothing was saved.";

/** No instance is connected, so the path could not be put to one. */
export const FOLDER_SAVE_NOT_CONFIGURED =
  "No Whisparr is connected, so Cove could not check that path.";

/** The save itself did not reach Cove, so nothing was established either way. */
export const FOLDER_SAVE_DID_NOT_REACH = "Cove could not save that path. Nothing was changed.";

/** The label on the disclosure holding the paths the instance was asked about. */
export function folderAgreementTriedSummary(count: number): string {
  return `Paths Cove asked Whisparr about (${String(count)})`;
}

/** The paths the instance was asked about, or a statement that it was asked about none. */
export function folderAgreementTriedSentence(tried: readonly string[]): string {
  if (tried.length === 0) {
    return "Cove asked Whisparr about no path at all.";
  }
  const asked =
    tried.length === 1 ? tried[0] : `${tried.slice(0, -1).join(", ")} and ${tried.at(-1)}`;
  return `Cove asked Whisparr about ${asked}.`;
}

/** The card holding the address Whisparr reports its imports to, named at its head. */
export const WEBHOOK_TITLE = "Import webhook";

/** What the card is for, stated under its title. */
export const WEBHOOK_DESCRIPTION = "The address Whisparr calls when it finishes an import.";

/** What the reader types: the address to hand the instance. */
export const WEBHOOK_ADDRESS = "Callback address";

/** The one reason to edit the address, and what to leave alone while doing it. */
export const WEBHOOK_ADDRESS_HELPER =
  "Change the address only if Whisparr reaches Cove somewhere other than you do. Leave the rest as it is.";

/** The control that puts the address on the clipboard, for pasting into Whisparr by hand. */
export const WEBHOOK_COPY = "Copy URL";

/** Why that control cannot be pressed while the field is empty. */
export const WEBHOOK_NOTHING_TO_COPY = "There is no callback address to copy.";

/** The register control while the registration it started is in flight. */
export const WEBHOOK_REGISTERING = "Registering…";

/** The same control at rest. */
export const WEBHOOK_REGISTER = "Register in Whisparr";

/** While whether the instance holds the callback is being read. */
export const WEBHOOK_READING_THE_STATUS = "Reading the callback status…";

/** That read failed, which is not the same as the instance holding no callback. */
export const WEBHOOK_STATUS_NOT_READ = "Cove could not read the callback status.";

/** The address is on the clipboard. */
export const WEBHOOK_ADDRESS_COPIED = "Copied.";

/** The clipboard was not there to write to, so the address has to be taken by hand. */
export const WEBHOOK_CLIPBOARD_NOT_REACHED =
  "Cove could not reach the clipboard - select the address above and copy it.";

/**
 * The registration request did not get through, which says nothing about what the instance holds.
 *
 * @param message Cove's own account of why the request failed
 */
export function webhookRegistrationFailedSentence(message: string): string {
  return `Cove could not register the callback: ${message}`;
}

/**
 * The instance answered and declined.
 *
 * Carries Whisparr's own words, so the refusal reports what it said rather than a guess.
 *
 * @param refusal what the instance said, in its own spelling
 */
export function webhookRefusedSentence(refusal: string): string {
  return `Whisparr refused it: ${refusal}`;
}

/**
 * The callback status before Cove has looked.
 *
 * Never merged with the not-registered sentence below. "Not looked yet" and "not there" send a
 * reader somewhere different.
 */
export const CALLBACK_NOT_CHECKED_YET = "Cove has not checked this instance for its callback yet.";

/** @see CALLBACK_NOT_CHECKED_YET */
export const CALLBACK_NOT_REGISTERED = "Cove's callback is not registered on this instance.";

/**
 * Registered, and nothing has arrived through it. Its own sentence rather than a shade of the one
 * below: the combination is the tell for an address mismatch, and plain success would hide it.
 */
export const CALLBACK_REGISTERED_WITH_NO_EVENTS =
  "Registered, but no import has reached Cove through it yet.";

/** @see CALLBACK_REGISTERED_WITH_NO_EVENTS */
export const CALLBACK_REGISTERED_AND_DELIVERING =
  "Registered, and imports are reaching Cove through it.";

/**
 * What this Cove may do to itself when Whisparr calls the address.
 *
 * Said before the gesture, not after it, and it opens on what the reader would lose rather than on
 * the mechanism: a warning nobody reaches the end of warns nobody. Whisparr checks a webhook by
 * posting to it. A Cove holding an owner account while sign-in is off can read that call as an
 * instance reachable from outside its own machine: it turns sign-in on, keeps it on, and signs out
 * the person configuring this. Whether it does depends on the address, where the call comes from
 * and the host's own trusted-host list, so this is a warning rather than a refusal.
 */
export const REGISTRATION_WOULD_LOCK_COVE_DOWN =
  "Registering may sign you out. Sign-in is off here, and the call Whisparr makes to check the address switches it on if it arrives from another machine. Set a password under Security & Access first.";

/** Why the register control cannot be pressed while the one already asked for is unanswered. */
export const REGISTRATION_IS_STILL_RUNNING = "This registration is still running.";

/** Why there is nothing to register: no address has been typed. */
export const NOTHING_TO_REGISTER = "There is no callback address to register.";

/**
 * The standing note, shown while deliveries still carry the callback secret in the address.
 *
 * There is no dismiss control. The note goes when the fact goes.
 */
export const LESS_PRIVATE_FORM_NOTE =
  "Imports are arriving with the callback secret in the address, where proxies and load balancers record it. Registering again from here moves it out of the address.";

/** Why a registration could not be attempted: the address is stored empty. */
export const REGISTER_NEEDS_A_SAVED_ADDRESS =
  "Enter the Whisparr address above and save it before registering the callback.";

/** @see REGISTER_NEEDS_A_SAVED_ADDRESS */
export const REGISTER_NEEDS_A_SAVED_KEY =
  "Enter the Whisparr API key above and save it before registering the callback.";

/** The card holding what a redelivery does to a scene already held, named at its head. */
export const UPGRADE_TITLE = "When Whisparr replaces a file";

/** What the card is for, stated under its title. */
export const UPGRADE_DESCRIPTION =
  "What happens to the scene Cove already holds when a better file arrives for it.";

/** What the reader chooses between. */
export const UPGRADE_FIELD = "Replacement files";

/** The default choice, named for what it leaves behind. */
export const UPGRADE_KEEP_BOTH = "Keep both files";

/** The other choice, under the same rule. */
export const UPGRADE_KEEP_ONLY_THE_NEW_FILE = "Keep only the new file";

/** What the default upgrade behaviour does, in the terms the reader sees the result in. */
export const UPGRADE_KEEPS_BOTH_FILES =
  "The new file joins the scene you already have, and Cove lists both until its own scan notices the old one is gone. Nothing is removed from disk.";

/** What the other upgrade behaviour does. */
export const UPGRADE_DROPS_THE_SUPERSEDED_FILE =
  "The new file joins the scene you already have and the file it replaces is dropped from it. That file stays on disk, for Whisparr to remove.";

/** No address or key was entered, so nothing was tried. Names the settings that would fix it. */
export const CONNECT_NOT_CONFIGURED =
  "Enter the Whisparr address and API key above, then test the connection.";

/** Something answered and turned the key down. Sends the user to the key, not to the address. */
export const CONNECT_KEY_REJECTED =
  "Whisparr turned that API key down. Check the key and test the connection again.";

/** Nothing answered at all. Says so plainly rather than implying the instance is empty. */
export function connectUnreachableSentence(address: string | null): string {
  return `Nothing answered at ${address ?? "that address"}. That is not the same as Whisparr having nothing; try again shortly.`;
}

/** Something answered, but not as the Whisparr API. */
export function connectNotTheWhisparrApiSentence(address: string | null): string {
  return `${address ?? "That address"} answered, but not as a Whisparr API. Check that it points at Whisparr itself.`;
}

/**
 * The Whisparr API answered on a version this product does not manage, or another application
 * answered in its place.
 */
export function connectVersionNotManagedSentence(
  version: string | null,
  otherApplication: string | null,
): string {
  const found = version ?? "an unnamed version";
  return otherApplication === null
    ? `That instance is Whisparr ${found}, which this extension does not manage.`
    : `That instance is ${otherApplication} ${found}, not Whisparr.`;
}

/** The card holding the instance Cove works against, named at its head. */
export const CONNECT_TITLE = "Connection";

/** What the card is for, stated under its title. */
export const CONNECT_DESCRIPTION = "The Whisparr instance Cove keeps in step with.";

/** Why the test cannot be started again while the one already asked for is unanswered. */
export const TEST_IS_STILL_RUNNING = "This test is still running.";

/** Why there is nothing to test: no address has been typed, so nothing could be reached. */
export const CONNECT_NEEDS_AN_ADDRESS_FIRST = "Enter the Whisparr address first.";

/**
 * Why a typed address cannot be tested on its own. A stored key cannot be sent back out, so a test
 * of an address that is not the stored one needs a key typed beside it.
 */
export const CONNECT_NEEDS_A_KEY_FOR_THIS_ADDRESS =
  "Enter the Whisparr API key to test this address.";

/** While what is stored for this generation is being read. */
export const CONNECT_READING_THE_STORED_CONNECTION = "Reading the stored connection…";

/** The read of what is stored failed, so the recorded lines state nothing. */
export const CONNECT_STORED_CONNECTION_NOT_READ =
  "Cove could not read what is stored for this connection.";

/** What the reader types: where Cove reaches the instance. */
export const CONNECT_ADDRESS = "Whisparr address";

/** What the address field takes, including the part a reader leaves out most often. */
export const CONNECT_ADDRESS_HELPER = "Where Cove itself reaches Whisparr, including the port.";

/** An example address, standing in the empty field. The e2e specs locate the field by it. */
export const CONNECT_ADDRESS_PLACEHOLDER = "http://whisparr:6969";

/** What the reader types: the key the instance issued. */
export const CONNECT_API_KEY = "API key";

/** What leaving the key field blank does. */
export const CONNECT_API_KEY_HELPER =
  "Leave blank to keep the key already stored for this generation.";

/** The control that drops the stored key at the next save. */
export const CONNECT_CLEAR_STORED_KEY = "Clear stored key";

/** The control that takes that back, on a draft that would drop the key. */
export const CONNECT_KEEP_STORED_KEY = "Keep stored key";

/** The test control while the test it started is in flight. */
export const CONNECT_TESTING = "Testing…";

/** The same control at rest. */
export const CONNECT_TEST = "Test connection";

/** What the next save does to the key, where the draft drops it. */
export const KEY_WILL_BE_REMOVED_ON_SAVE = "Key will be removed when you save";

/** What the next save does to the key, where the draft carries a typed one. */
export const NEW_KEY_WILL_BE_SAVED = "New key will be saved";

/**
 * What the test is working on, said while it runs.
 *
 * @param address the address being tested, as the form holds it
 */
export function connectTestingSentence(address: string): string {
  return `Testing ${address}`;
}

/**
 * The test itself never ran, which says nothing about the instance.
 *
 * @param message Cove's own account of why the request failed
 */
export function connectTestDidNotRunSentence(message: string): string {
  return `Cove could not run the test: ${message}`;
}

/**
 * The address answered as the generation the form is not editing, so nothing was saved and the
 * other generation's own form is where that address belongs.
 *
 * @param detected the generation that answered, as the reader sees it named
 * @param version the version that instance reported, read off its own answer
 * @param drafted the generation the form is editing, as the reader sees it named
 */
export function connectOtherGenerationSentence(
  detected: string,
  version: string | null,
  drafted: string,
): string {
  return `That address answered as ${detected} ${version ?? ""}, not ${drafted}. Nothing was saved - select ${detected} above to configure it there.`;
}

/**
 * The instance answered as the generation the form is editing.
 *
 * The version is the instance's own string, unformatted. Reformatting it would report a version no
 * instance runs.
 *
 * @param version the version the instance reported
 * @param generation the generation it answered as
 */
export function connectedSentence(version: string | null, generation: string | null): string {
  return `Connected to Whisparr ${version ?? ""} (${generation ?? ""})`;
}

/** That a key is held for a generation. Never any part of the key itself. */
export const KEY_IS_SET = "Key is set";

/** @see KEY_IS_SET */
export const KEY_NOT_STORED = "Key not stored";

/** Which of the two Whisparr generations the form edits. */
export const CONNECT_GENERATION = "Whisparr generation";

/** That the two generations are configured apart, and what choosing one here costs. */
export const CONNECT_GENERATION_HELPER =
  "Each generation keeps its own address and key. Saving moves Cove to the one selected here.";

/** The word marking the generation the form holds, so the mark is not a colour alone. */
export const GENERATION_SELECTED = "Selected";

/**
 * The control that puts the other generation into the form.
 *
 * @param label the generation it selects, as the reader sees it named
 */
export function selectGenerationName(label: string): string {
  return `Select ${label}`;
}

/** What an option's address line reads before the stored values arrive. */
export const GENERATION_ADDRESS_NOT_READ_YET = "Not read yet";

/** What it reads once they have arrived and hold no address. */
export const GENERATION_NO_ADDRESS_STORED = "No address stored";

/** The newer generation's name, declared once so two surfaces cannot name it differently. */
export const GENERATION_V3_NAME = "Whisparr v3 (Eros)";

/** @see GENERATION_V3_NAME */
export const GENERATION_V2_NAME = "Whisparr v2";

/** The recorded-version line while no version has ever been read off the instance. */
export const VERSION_NOT_VERIFIED_YET = "Whisparr version not verified yet";

/**
 * The same line once a version has been read.
 *
 * The version is the instance's own string, unformatted. Reformatting it would report a version no
 * instance runs.
 *
 * @param version the version the instance reported
 * @param verifiedAt when it was read, as the page words an age
 */
export function recordedVersionSentence(version: string, verifiedAt: string): string {
  return `Whisparr reported ${version} · verified ${verifiedAt}`;
}

/** The last-reachable line before the instance has answered anything at all. */
export const INSTANCE_HAS_NOT_ANSWERED_YET = "Whisparr has not answered yet";

/**
 * The same line once it has. Measures when the instance last answered, which is not when its
 * version was read, so the two lines are never merged into one.
 *
 * @param when how long ago it answered, as the page words an age
 */
export function lastReachableSentence(when: string): string {
  return `Whisparr last reachable ${when}`;
}

/** What the bar at the foot of the settings page heads its summary of the edits with. */
export const UNSAVED_CHANGES = "Unsaved changes";

/** The four names the save bar calls an unsaved field by, each named as its own control is. */
export const FIELD_NAME_GENERATION = "the Whisparr generation";

/** @see FIELD_NAME_GENERATION */
export const FIELD_NAME_ADDRESS = "the Whisparr address";

/** @see FIELD_NAME_GENERATION */
export const FIELD_NAME_API_KEY = "the API key";

/** @see FIELD_NAME_GENERATION */
export const FIELD_NAME_UPGRADE_BEHAVIOR = "the replacement-file behaviour";

/** Said whenever the generation is unsaved, because saving it reloads the page. */
export const SAVING_THE_GENERATION_RELOADS =
  "Saving changes the generation Cove uses and reloads the page.";

/**
 * Which fields the save bar reports unsaved.
 *
 * @param names the field names, in the order the page draws the controls that hold them
 */
export function unsavedFieldsSentence(names: readonly string[]): string {
  const list =
    names.length === 1 ? names[0] : `${names.slice(0, -1).join(", ")} and ${names.at(-1)}`;
  return `${list.charAt(0).toUpperCase()}${list.slice(1)} ${
    names.length === 1 ? "is" : "are"
  } not saved yet.`;
}

/** What the bar says once a save has landed. */
export const SETTINGS_SAVED = "Settings saved.";

/**
 * A save that did not land, said on the bar that sent it.
 *
 * @param message Cove's own account of why the save failed
 */
export function settingsSaveFailedSentence(message: string): string {
  return `Cove could not save: ${message}`;
}

/** Why nothing on the settings page can act yet: the stored connection is still on its way. */
export const CONNECTION_IS_STILL_BEING_READ = "Cove is still reading the stored connection.";

/** Why nothing on the page can act at all: what is stored could not be read. */
export const NOTHING_CAN_ACT_ON_AN_UNREAD_CONNECTION =
  "Cove could not read the stored connection, so nothing here can act on it yet.";

/** The card holding how much of the library the instance knows about, named at its head. */
export const SYNC_TITLE = "Sync your library to Whisparr";

/** What the count control is called before any result exists. */
export const SYNC_COUNT = "Count what would sync";

/** The three count rows, each naming its own noun so no number has a plural to disagree with. */
export const SYNC_NOT_YET_IN_WHISPARR = "Not yet in Whisparr";

/** @see SYNC_NOT_YET_IN_WHISPARR */
export const SYNC_ALREADY_IN_WHISPARR = "Already in Whisparr";

/** @see SYNC_NOT_YET_IN_WHISPARR */
export const SYNC_SKIPPED_CANNOT_BE_IDENTIFIED = "Skipped, cannot be identified";

/**
 * The row drawn from the one above it: of what Whisparr already holds, how much it records no file
 * for. Those are what a run still has to hand over once there is nothing left to register.
 *
 * @see SYNC_NOT_YET_IN_WHISPARR
 */
export const SYNC_NO_FILE_RECORDED = "Of those, with no file recorded";

/** What the skipped row means, and what a reader can do about it. */
export const SYNC_SKIPPED_CANNOT_BE_REGISTERED =
  "A scene with no metadata id cannot be registered. Identify more of your library and count again.";

/**
 * The same, where the run registers the studios a library covers rather than its scenes.
 *
 * @see SYNC_SKIPPED_CANNOT_BE_REGISTERED
 */
export const SYNC_SITE_SKIPPED_CANNOT_BE_REGISTERED =
  "A studio cannot be registered where your library carries no metadata id for it, or where the " +
  "metadata source names no site for that id. Identify more of your library and count again.";

/**
 * When the figures above were taken, and what to do about the ones the run skips.
 *
 * @param when how long ago the count was taken, as the page words an age
 * @param remedy what a reader can do about the skipped row, resolved from what the run registers
 */
export function syncCountedSentence(when: string, remedy: string): string {
  return `Counted ${when}. ${remedy}`;
}

/** While the count runs. */
export const SYNC_COUNTING = "Counting what would sync.";

/** Before any count. */
export const SYNC_NOTHING_COUNTED_YET = "Nothing has been counted yet.";

/** A count that did not finish, however it failed. */
export const SYNC_COUNT_DID_NOT_FINISH =
  "Cove could not finish counting what would sync. Nothing was changed; try again shortly.";

/** Why the count control cannot be pressed while a count is in flight. */
export const SYNC_IS_COUNTING = "Cove is counting what would sync.";

/** What the sync control is called, and the words its confirmation is titled with. */
export const SYNC_LIBRARY = "Sync library to Whisparr";

/** What the sync card offers, stated under its title. */
export const SYNC_REGISTERS_THE_SCENES_YOU_OWN =
  "Register the scenes you already own, so Whisparr knows about them, " +
  "and link the files you own to what it holds.";

/**
 * The same, where the run registers the studios a library covers rather than its scenes.
 *
 * @see SYNC_REGISTERS_THE_SCENES_YOU_OWN
 */
export const SYNC_REGISTERS_THE_STUDIOS_YOU_OWN =
  "Register the studios in your library, so Whisparr knows about them.";

/** The monitor choice, made at press time rather than stored. */
export const SYNC_ALSO_MONITOR = "Also monitor what it syncs";

/** Why the sync control cannot be pressed before a count exists. */
export const SYNC_NEEDS_A_COUNT_FIRST =
  "Count what would sync first, so this can say how many scenes it will offer.";

/**
 * The same, where the run registers the studios a library covers rather than its scenes.
 *
 * @see SYNC_NEEDS_A_COUNT_FIRST
 */
export const SYNC_SITE_NEEDS_A_COUNT_FIRST =
  "Count what would sync first, so this can say how many studios it will offer.";

/**
 * Why there is nothing for the sync control to do.
 *
 * A run registers and then hands files over, so both halves have to be spent before there is
 * nothing left. Naming registration alone left a reader with a half-done library and a control
 * they could not press.
 */
export const SYNC_NOTHING_LEFT_TO_SYNC =
  "Whisparr already holds every scene in your library that carries a metadata id, and records a " +
  "file for each of them.";

/** Why nothing on the sync side can act while a run is in flight. Points at the progress surface. */
export const SYNC_ALREADY_RUNNING =
  "A library sync is already running. Its progress is in Cove's job list.";

/** Why nothing on the sync side can act while the enqueue itself is in flight. */
export const SYNC_IS_STARTING = "Cove is starting the sync.";

/** What the section says after the press, and the whole of what it says. */
export const SYNC_RUNS_IN_THE_JOB_DRAWER =
  "This runs in the background. Its progress appears in Cove's job list.";

/** The reason the confirmation exists, stated at every size. */
export const SYNC_DOWNLOADS_NOTHING = "Registering a scene in Whisparr downloads nothing.";

/**
 * The same, where the run registers the studios a library covers rather than its scenes.
 *
 * @see SYNC_DOWNLOADS_NOTHING
 */
export const SYNC_SITE_DOWNLOADS_NOTHING = "Registering a studio in Whisparr downloads nothing.";

/** What the confirmation covers where the run offers one scene. */
export const SYNC_OFFERS_ONE_SCENE = "This offers the 1 scene you own to Whisparr";

/**
 * The same, where the run registers the studios a library covers rather than its scenes.
 *
 * @see SYNC_OFFERS_ONE_SCENE
 */
export const SYNC_OFFERS_ONE_SITE = "This offers the 1 studio in your library to Whisparr";

/**
 * What the confirmation covers at any size but one.
 *
 * @param grouped the figure, already grouped, so this module stays free of a number format
 */
export function syncOffersScenes(grouped: string): string {
  return `This offers all ${grouped} scenes you own to Whisparr`;
}

/**
 * The same, where the run registers the studios a library covers rather than its scenes.
 *
 * @param grouped the figure, already grouped
 * @see syncOffersScenes
 */
export function syncOffersSites(grouped: string): string {
  return `This offers all ${grouped} studios in your library to Whisparr`;
}

/**
 * What the confirmation says about the entries the run passes over, where the count found one.
 *
 * Follows what the run covers rather than standing alone, so it opens on a comma. Names no noun,
 * so one declaration serves whichever the run registers.
 */
export const SYNC_SKIPS_ONE = ", and skips 1 that cannot be registered";

/**
 * The same at any other size.
 *
 * @param grouped the figure, already grouped
 * @see SYNC_SKIPS_ONE
 */
export function syncSkipsMany(grouped: string): string {
  return `, and skips ${grouped} that cannot be registered`;
}

/**
 * What the run still has to hand over, stated where the count found any.
 *
 * Reads at one as it does at any other size: the figure carries no noun after it, so there is no
 * plural to disagree with, and the same sentence serves whichever noun the run registers.
 *
 * Names where the figure was counted rather than saying "of them". It follows the clause about
 * what the run skips, so "them" read as the skipped ones, which is the opposite set.
 *
 * @param grouped the figure, already grouped
 */
export function syncStillToHandOver(grouped: string): string {
  return `Whisparr holds ${grouped} with no file recorded yet.`;
}

/** What the monitor choice adds to the run, where the run registers scenes. */
export const SYNC_ALSO_MONITORS_EACH = "It also marks each of them monitored.";

/**
 * What the monitor choice adds to the run where it registers studios: the scenes the reader owns on
 * each of them, which is what gets marked there rather than the studios themselves.
 */
export const SYNC_SITE_ALSO_MONITORS_THE_SCENES_ON_THEM =
  "It also marks the scenes you own on them monitored.";

/** What the run does about monitoring with the choice off, whatever it registers. */
export const SYNC_MONITORS_NOTHING = "It monitors nothing.";

/**
 * What the run does with the files after it has registered, on the generation whose run links.
 *
 * States the mechanism and both its preconditions, as the per-entity control does. Both settings
 * are the instance's: with hard links off every matched file would be copied in full rather than
 * linked, and with renaming on the instance moves what it takes in out of the folder it was handed.
 * What happens when a precondition is unmet is not spelled out: "needs" carries it, and the run's
 * own ending names each entry it left alone.
 */
export const SYNC_ALSO_LINKS_WHAT_YOU_OWN =
  "It then links each file you own into the folder Whisparr keeps for it, using no extra disk. " +
  "This needs Whisparr's hard-link setting on and its renaming off.";

/**
 * What the run does with the files after it has registered, on the generation that builds the
 * folders itself.
 *
 * The second names are Cove's own act and no setting of Whisparr's is a precondition for them.
 * Handing the folder over is a separate act with the same two preconditions the other generation's
 * linking has, so the settings are named on that half alone.
 */
export const SYNC_SITE_ALSO_LINKS_WHAT_YOU_OWN =
  "It then gives each one a folder holding a second name for every file you own, which costs no extra disk and moves nothing. Handing those folders to Whisparr needs its hard-link setting on and its renaming off.";

/** An age under a minute, which every surface that states one reads the same way. */
export const JUST_NOW = "just now";

/** An age in minutes, which reads "min" whatever the count. */
export function minutesAgo(count: number): string {
  return `${String(count)} min ago`;
}

/** An age in hours. */
export function hoursAgo(count: number): string {
  return `${String(count)} hour${count === 1 ? "" : "s"} ago`;
}

/**
 * The month names the date form draws on.
 *
 * A table rather than the platform locale formatter: a locale-dependent rendering shows the same
 * instant differently in two places and gives a test nothing fixed to assert.
 */
const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

/**
 * An instant too old to read as an age.
 *
 * @param day the day of the month
 * @param monthIndex the month, counted from zero as the platform counts it
 * @param year the full year
 */
export function dateSentence(day: number, monthIndex: number, year: number): string {
  return `${String(day)} ${MONTHS[monthIndex]} ${String(year)}`;
}
