/**
 * The product's specified sentences.
 *
 * These are specified content, not placeholder text to be improved later: the wording is part of what
 * the product promises, so a caller reads a constant here rather than writing its own phrasing. A
 * sentence is declared ONCE and every caller reads that declaration; a second copy elsewhere is what
 * drifts silently when one of the two is corrected.
 *
 * Some sentences name a value the instance sent back. Those are functions rather than constants, so
 * the value arrives as an argument and the sentence itself is still declared in one place.
 */

/**
 * The library toolbar control's own name while the card badges are hidden.
 *
 * The control carries the product's mark instead of a word, so its accessible name is the only name
 * it has.
 */
export const SHOW_WHISPARR_STATUS = "Show Whisparr status";

/** The same control's name once the badges are on. */
export const HIDE_WHISPARR_STATUS = "Hide Whisparr status";

/**
 * Nothing could be reached, said once for the page on the control that asked.
 *
 * The second sentence is the point of the message. A page of cards that simply drew no badge would
 * read as a library Whisparr holds nothing for, which is a different fact and the one a reader would
 * act on.
 */
export const WHISPARR_STATUS_COULD_NOT_BE_READ =
  "Cove could not reach Whisparr, so no card can show a status. That is not the same as Whisparr holding nothing.";

/**
 * Nothing is connected, so nothing was asked, said once for the page on the control that asked.
 *
 * The second sentence keeps it apart from an instance that answered. A page of cards drawing no badge
 * would otherwise read as a Whisparr that holds nothing for the library.
 */
export const NO_WHISPARR_CONNECTED =
  "No Whisparr is connected, so no card can show a status. Nothing was asked of Whisparr.";

/**
 * The connected Whisparr keeps no record of the cards on this page, so it was not asked.
 *
 * Names no setting and no version, because neither changes the answer. The second sentence is the
 * point: nothing failed and nothing is missing from Whisparr's own side.
 */
export const WHISPARR_KEEPS_NO_RECORD_OF_THESE =
  "The connected Whisparr keeps no record of these, so no card can show a status. It was not asked.";

/**
 * The status request itself never answered, said once for the page on the control that made it.
 *
 * Distinct from an instance Cove reached and could not read: here Cove's own read did not complete,
 * so whether Whisparr was asked at all is not established. Claiming either would be a fact nothing
 * answered.
 */
export const THE_STATUS_READ_DID_NOT_COMPLETE =
  "Cove could not complete the status read, so no card can show a status. Whisparr may not have been asked at all.";

/**
 * The badges are on where the display mode draws none, said on the control that turned them on.
 *
 * The host mounts a card slot in its grid display mode only, and the control sits in the toolbar of
 * every mode. `Grid` is the host's own visible label for the mode that draws them.
 */
export const NO_PLACE_FOR_A_CARD_STATUS_HERE =
  "This display mode has no place for a per-card status. Switch to the Grid display mode to see it.";

/**
 * The name every menu row carries, which is not the name the scene tab's control of the same verb
 * carries.
 *
 * A row names its verb alone, because the panel above it is headed with the product's name and the
 * count the choice covers. The scene tab's controls sit in a rail beside Cove's own, where the
 * product's name is what says who acts. The two sets are declared apart and neither is derived from
 * the other.
 */
export const MENU_ADD = "Add";

/** Sits beside {@link MENU_UNMONITOR}, so the pair reads as one axis at a glance. */
export const MENU_MONITOR = "Monitor";

export const MENU_UNMONITOR = "Unmonitor";

export const MENU_EXCLUDE = "Exclude";

/** The scene tab's monitor control, while the instance is not monitoring the scene. */
export const MONITOR_IN_WHISPARR = "Monitor in Whisparr";

/**
 * The entity control's own name while the entity is not monitored in Whisparr.
 *
 * The control carries the product's mark instead of a word, so its accessible name is the only name
 * it has. A filled two-tone disc cannot inherit `currentColor`, so it cannot carry the state either.
 */
export const WHISPARR_NOT_MONITORED = "Whisparr, not monitored";

/** The same control's name once the connected instance monitors the entity. */
export const WHISPARR_MONITORED = "Whisparr, monitored";

/**
 * The entity's monitored state could not be read.
 *
 * The second sentence is the point of the message. A control that fell back to its unmonitored
 * appearance would report, confidently, that Whisparr is not monitoring the entity - which is a
 * different fact from not knowing, and the one that would make a reader stop looking.
 */
export const MONITORING_COULD_NOT_BE_READ =
  "Cove could not read what Whisparr monitors for this. That is not the same as Whisparr monitoring nothing.";

/**
 * An action that never reached the instance.
 *
 * Says that nothing changed, because the alternative reading - that it changed and the answer was
 * lost - is the one a reader will otherwise assume and act on.
 */
export const ACTION_DID_NOT_REACH_WHISPARR =
  "Cove could not carry that out. Nothing here was changed; try again shortly.";

/**
 * An action this build of the extension does not carry out.
 *
 * Names the version rather than the reason, because no setting and no instance changes the answer.
 */
export const ACTION_ABSENT_IN_THIS_VERSION =
  "This version of Whisparr Sync does not carry out this action.";

/**
 * The narrower monitor scope.
 *
 * Says what pressing the row does. Whisparr's own two names for the setting are names for a flag,
 * and a reader choosing between them cannot tell from them what either one will go and do. Its
 * other monitor wording is not carried across either: its own dropdown renders unsubstituted
 * localization keys, so mimicry stops here.
 */
export const SCOPE_FUTURE_SCENES = "Monitor - new releases only";

/** The wider monitor scope, named for what it does with what Whisparr already lists. */
export const SCOPE_ALL_SCENES = "Monitor - all scenes (queue back-catalogue)";

/**
 * What the wider scope costs, stated where the scope is chosen rather than after it is taken.
 *
 * Names the cost the reader pays rather than the flag that is written, because a flag is not
 * something anyone budgets for.
 */
export const ALL_SCENES_MARKS_THE_BACK_CATALOGUE =
  "Monitoring all scenes marks every scene Whisparr already lists for this entity as wanted, which spends indexer traffic and disk.";

/**
 * That the wider scope is a one-way door, stated where the scope is chosen.
 *
 * For the generation whose date gate applies only to what a later refresh adds. The generation that
 * rewrites every flag on a scope change does not render this, because there it would be false.
 */
export const ALL_SCENES_IS_NOT_UNDONE_BY_A_LATER_SCOPE_CHANGE =
  "Narrowing the scope back to new releases only does not undo this: a scene that is already wanted stays wanted.";

/**
 * What the search costs, stated where it is chosen rather than after it is taken.
 *
 * The one row on the selection bar that can download. Names what Whisparr goes and does rather than
 * the command that is sent.
 */
export const SEARCH_ALL_MONITORED_SPENDS_TRAFFIC_AND_DISK =
  "Whisparr looks for everything these entities monitor and does not hold, and takes in what it finds, which spends indexer traffic and disk.";

/** Why nothing was linked. Names the setting, because turning it on is what changes the answer. */
export const REFLECT_OWNED_SKIPPED =
  "Skipped: with Whisparr's hard-link setting off each file would be copied rather than linked, and would use disk twice.";

/**
 * Why nothing was linked when the setting itself could not be read.
 *
 * Its own sentence rather than the one above, which states a setting is off. Reporting a value
 * nobody read as a value that was read is the reading a user would act on.
 */
export const REFLECT_OWNED_SKIPPED_SETTING_UNREADABLE =
  "Skipped: Cove could not read Whisparr's hard-link setting, so it could not establish that linking these files would cost no extra disk.";

/**
 * The same scene tab control once the instance monitors the scene. Named for what it does, not for
 * the flag it writes.
 */
export const STOP_MONITORING_IN_WHISPARR = "Stop monitoring in Whisparr";

/**
 * Where the result of a selection appears.
 *
 * Stated before the work starts, because nothing on the page the reader is looking at changes when
 * it finishes.
 */
export const BULK_REPORTS_IN_THE_JOB_DRAWER =
  "This runs in the background. Its progress, and its result for each entity, appear in Cove's job list.";

/**
 * Nothing could be offered, because what the connected Whisparr can do was not read.
 *
 * Offering a guessed set would put a verb in front of the reader that the instance cannot honour,
 * and the refusal that followed would read as a fault in the product.
 */
export const BULK_ACTIONS_COULD_NOT_BE_OFFERED =
  "Cove could not read what the connected Whisparr can do, so it offered nothing. Nothing was changed; try again shortly.";

/**
 * How many entities one run may carry.
 *
 * The route's own bound, declared here because the sentence below names it. A test compares this
 * number against the route's `MaxEntityIdsPerRequest`, which is what stops the two drifting apart in
 * silence. This library already holds more entities of one kind than the bound admits, so a reader
 * who selects everything reaches it.
 */
export const MAX_ENTITY_IDS_PER_REQUEST = 1000;

/**
 * Why a selection larger than the bound did nothing.
 *
 * Names the bound rather than hiding it. A reader who selected everything cannot act on "too many",
 * and acting on part of the selection at a time is what works.
 */
export const BULK_SELECTION_IS_OVER_THE_BOUND = `Cove acts on at most ${String(MAX_ENTITY_IDS_PER_REQUEST)} entities in one run, and you selected more. Nothing was changed; select fewer and repeat over the rest.`;

/**
 * Any other refusal of the whole gesture.
 *
 * Quotes nothing the instance said. This generation answers a refusal with a body carrying a full
 * stack trace, and a sentence built from that body would put it in front of the reader.
 */
export const BULK_SELECTION_WAS_NOT_STARTED =
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

/**
 * Why a control is unavailable while the last thing asked for is still on its way.
 *
 * A dimmed control with nothing to hear is the defect this exists to prevent, and a transient reason
 * needs one as much as a permanent one does.
 */
export const WAITING_FOR_WHISPARR = "Waiting for Whisparr to answer the last thing you asked for.";

/**
 * Nothing is connected, said on a page that is not the settings page.
 *
 * Names where to go, because the reader cannot see the address field from here.
 */
export const NO_INSTANCE_CONNECTED =
  "No Whisparr instance is connected. Connect one on the Whisparr Sync settings page.";

/**
 * The entity carries no identifier the connected instance could be given.
 *
 * Which link is needed depends on which instance is connected, so the sentence names neither and the
 * reader checks the link chips this page already shows.
 */
export const NO_IDENTITY_IN_THIS_NAMESPACE =
  "Cove holds no link for this entity that the connected Whisparr can identify it by.";

/**
 * The entity carries several links the connected instance would read as the same source, naming
 * different entities.
 *
 * Names the page the reader fixes it on rather than the instance, because nothing in Whisparr is
 * wrong here: Cove holds two links and only one of them can be the right one.
 */
export const SEVERAL_IDENTITIES_IN_THIS_NAMESPACE =
  "Cove holds more than one conflicting link for this entity, so which one Whisparr should use is " +
  "unclear. Remove the links that do not belong on this entity's page in Cove.";

/** The instance offers no quality profile, so nothing could be composed to send. */
export const INSTANCE_OFFERS_NO_QUALITY_PROFILE =
  "Whisparr offers no quality profile, so nothing was sent. Add one in Whisparr and try again.";

/** The instance offers no library root, so nothing could be composed to send. */
export const INSTANCE_OFFERS_NO_ROOT_FOLDER =
  "Whisparr offers no root folder, so nothing was sent. Add one in Whisparr and try again.";

/**
 * The instance answered and declined.
 *
 * Says nothing about why. This generation answers a refused add with a stack trace, so its own words
 * are never read and never repeated.
 */
export const INSTANCE_REFUSED = "Whisparr would not do this. Nothing here was changed.";

/**
 * The instance answered, and the answer was past what this extension reads at once.
 *
 * Names this extension as the party that stopped, because Whisparr did nothing wrong. Names no byte
 * count, no buffer and no setting: none of the three is something the reader can act on.
 *
 * Claims nothing about what did or did not happen. The read this arrives from can be the one taken
 * straight after an accepted change, where a claim that nothing changed would be false.
 */
export const INSTANCE_ANSWER_WAS_TOO_LARGE_TO_READ =
  "Whisparr's answer was larger than this extension reads at once. Your Whisparr answered correctly. Reload the page for its current state.";

/**
 * The instance answered, and holds no such entry.
 *
 * The noun is "entry" rather than "scene": both routes that state this act on a studio or a
 * performer. Both read before sending anything, and both are offered only on an entry Whisparr was
 * holding when the page opened, so "no longer" and "nothing to act on" are each true where this
 * appears.
 */
export const INSTANCE_HOLDS_NO_SUCH_ENTRY =
  "Whisparr no longer holds this entry, so there was nothing to act on. Reload the page for its current state.";

/**
 * The change was accepted, and the read taken straight after it does not report it.
 *
 * Says the change was accepted, because it was, and says nothing about the state that followed,
 * because the read that would have established it is the one that disagreed.
 */
export const INSTANCE_DID_NOT_REPORT_THE_CHANGE =
  "Whisparr accepted the change but does not report it. Reload the page for its current state.";

/**
 * The version-gap sentence, for a capability the connected generation does not have.
 *
 * Never wording that suggests migrating, and never a generic "not supported".
 */
export const CAP_UNAVAILABLE_ON_THIS_GENERATION = "Currently available on Whisparr v3 (Eros)";

/**
 * A search asked for on an entity Whisparr does not hold. A true statement, not a failure, so it
 * renders differently from a failed request.
 */
export const SEARCH_WITH_NO_ENTRY =
  "Whisparr has no entry for this scene yet, so there is nothing to search for - mark it wanted first.";

/** The product's own name, drawn beside the state chip in the scene tab's header. */
export const SCENE_HEADER_WHISPARR = "Whisparr";

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

/**
 * A search asked for on a scene the instance holds no entry for, on the scene's own tab.
 *
 * A second sentence for the meaning `SEARCH_WITH_NO_ENTRY` carries on the catalogue tab. The next
 * step differs by surface, and naming where the reader fixes it is what the requirement asks for.
 */
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

/**
 * What confirms a search, read back off the instance by the command's own id.
 *
 * Claims that Whisparr holds the command and nothing about a download, because the read that
 * licenses it establishes only the first.
 */
export const SCENE_SEARCH_IS_WITH_WHISPARR =
  "Whisparr has the search. What it finds arrives the same way every other import does.";

/**
 * How many scenes one search run may carry.
 *
 * The route's own lower bound, declared here because the sentence below names it. One press of the
 * search row becomes one search per scene against every indexer the instance has, so its cost
 * multiplies outside Cove in a way the other four rows' does not. A test compares this number
 * against the route's `MaxSceneSearchIdsPerRequest`, which is what stops the two drifting apart in
 * silence.
 */
export const MAX_SCENE_SEARCH_IDS_PER_REQUEST = 100;

/**
 * Why a selection larger than the search row's own bound did nothing.
 *
 * Names the limit that applied rather than the general one. A reader refused at 100 who is told
 * about 1000 has been told something that is not true of what they just did.
 */
export const BATCH_SEARCH_IS_OVER_THE_BOUND = `A search runs against every indexer Whisparr has, so Cove searches at most ${String(MAX_SCENE_SEARCH_IDS_PER_REQUEST)} scenes in one run, and you selected more. Nothing was sent; select fewer and repeat over the rest.`;

/**
 * What a `{provider}` slot reads as before any page has answered.
 *
 * Which source is read follows the connected generation and is named by the page, so a read that
 * produced no page has no name to fill the slot with.
 */
export const THE_METADATA_SOURCE = "your metadata source";

/**
 * A missing-check whose provider did not answer.
 *
 * The second sentence is the point of the message. `{provider}` and `{entity}` are the specified
 * text: the surface that renders this fills them with the names it holds.
 */
export const PROVIDER_UNREACHABLE =
  "Couldn't reach {provider} to check what's missing for {entity}. This isn't the same as owning everything - try again shortly.";

/** A missing-check that succeeded and found nothing missing. Only for a check that did succeed. */
export const NOTHING_MISSING = "You own every scene {provider} lists for {entity}.";

/**
 * A page whose scenes were all owned, in a catalogue that still holds others.
 *
 * Owned scenes are removed after a page arrives and a page is never topped back up, so a page can
 * empty completely while later pages still hold scenes. That is not an empty catalogue, and saying
 * so would be false against the count beside it.
 */
export const EVERY_SCENE_ON_THIS_PAGE_IS_OWNED =
  "You own every scene on this page. Later pages hold the ones you do not.";

/**
 * A parent studio read without its sub-studios, which the provider attributes every scene to.
 *
 * Held apart from owning everything, which it is not: the catalogue read was empty because the
 * scenes sit one level down, and NOTHING_MISSING would be vacuously true of the query and false to
 * a reader. The control named here is Cove's own, quoted as the page labels it so it points at
 * something findable.
 */
export const NO_SCENES_WITHOUT_SUB_STUDIOS =
  "{provider} lists this studio's scenes under its sub-studios. Turn on “Include sub-studio content” above to see them.";

/**
 * Cove names no metadata source, so there is nothing to read a catalogue from.
 *
 * About Cove's own configuration rather than a capability an instance lacks, which is why it names
 * a setting and where to find it. `{provider}` and `{entity}` are filled by the surface.
 */
export const NO_METADATA_PROVIDER_CONFIGURED =
  "Set up a {provider} metadata source in Cove (Settings → Scraping → Metadata servers) to discover {entity}'s catalogue.";

/** The entity carries no identifier the provider issued, and its name matched nothing exactly. */
export const NO_PROVIDER_ID_FOR_ENTITY =
  "No {provider} id for {entity}, so there is no catalogue to check.";

/** A title search over the whole catalogue that matched nothing. Renders with a way to clear it. */
export const NO_TITLES_MATCH = "No titles match that search.";

/**
 * A filtered catalogue that matched nothing.
 *
 * Held apart from owning everything, which is a different fact and the one a reader would act on. A
 * link made against another provider carries filter values this one never issued, so it matches
 * nothing for a reason that is not ownership.
 */
export const NO_SCENES_MATCH_THESE_FILTERS =
  "No scenes match these filters. Clear them to see the whole catalogue.";

/**
 * The catalogue was read and Whisparr was not.
 *
 * The second sentence is the point: the grid below is complete and only the status column is
 * missing, so a reader does not take the page for a short one. Renders with a way to try again.
 */
export const WHISPARR_STATUS_NOT_READ =
  "Cove could not reach Whisparr, so it could not read a status for these. The catalogue below is still complete.";

/**
 * The connected Whisparr holds no per-scene records at all.
 *
 * Renders without a retry, because nothing clears it. Names no setting and no version: neither
 * changes the answer.
 */
export const WHISPARR_KEEPS_NO_SCENE_RECORDS =
  "The connected Whisparr keeps no per-scene records, so Cove cannot read a status for these. The catalogue below is still complete.";

/**
 * What the figure beside a catalogue counts.
 *
 * The tab badge takes a number and has no room for this, so the line above the grid carries it.
 *
 * Written to stand alone: the range it refers to is stated in the toolbar above that line, so this
 * names the total it is about rather than continuing from it.
 */
export const COUNT_IS_THE_CATALOGUE_SIZE =
  "That total is the scenes {provider} lists for {entity}, not the number you are missing.";

/**
 * The catalogue tab's own name, drawn at the left of its toolbar.
 *
 * The same word the manifest advertises the tab under. The host draws that one on the tab strip and
 * hands the tab no name of its own, so the toolbar states it.
 */
export const MISSING_TAB_HEADING = "Missing";

/**
 * The name of the card control that marks one scene wanted.
 *
 * The control carries a glyph and no word, so this is the only name it has. It names the scene as
 * well as the verb, because a page draws forty of the same control and a name carrying the verb
 * alone reads identically on every one of them.
 */
export function monitorSceneName(title: string): string {
  return `Monitor ${title} in Whisparr`;
}

/** The name of the card control that asks Whisparr to look for one scene, under the same rule. */
export function searchSceneName(title: string): string {
  return `Search Whisparr for ${title}`;
}

/**
 * The name of the card link that opens one scene where its source shows it.
 *
 * The cover carries no text and the title carries only the title, so neither says that following it
 * leaves Cove. The source is not named, because which source answered follows the connected
 * generation and a name written here would be wrong on the other one.
 */
export function openSceneName(title: string): string {
  return `Open ${title} at your metadata source`;
}

/**
 * A control's name while its own request is unanswered.
 *
 * The reason joins the name rather than riding beside it: a control with no text has nothing for a
 * separate carrier to sit next to.
 */
export function nameWhileWaiting(name: string): string {
  return `${name}. ${WAITING_FOR_WHISPARR}`;
}

/**
 * What a facet control reads while nothing is picked in it.
 *
 * The control names the value in force, so with none in force it names the whole of what the menu
 * covers rather than what pressing it opens. The menu's own name arrives from the source.
 */
export function facetCoversEverything(menuLabel: string): string {
  return `All ${menuLabel.toLowerCase()}`;
}

/**
 * The range a page covers, out of the whole catalogue.
 *
 * `atCeiling` renders the total as a floor rather than a count: one provider reports a total it
 * will not serve past, and the badge beside this line takes a number and cannot say so.
 */
export function countLine(from: number, to: number, total: number, atCeiling: boolean): string {
  return `${String(from)}–${String(to)} of ${String(total)}${atCeiling ? "+" : ""}`;
}

/**
 * What a menu carrying part of the source's list says at the control, with nothing typed.
 *
 * The source decides how many values it serves for one read, so `reported` is the source's own
 * figure and `shown` is what arrived.
 */
export function facetMenuBound(shown: number, reported: number): string {
  return `This menu carries ${String(shown)} of ${String(reported)} values. The rest cannot be picked here.`;
}

/**
 * What the same menu says once a fragment is in force.
 *
 * The counts are of the values matching what was typed rather than of the whole list, so the two
 * sentences are worded apart: the same figures under the other wording would say the rest cannot be
 * reached, when typing more of the name is what reaches them.
 */
export function facetMatchesBound(shown: number, reported: number): string {
  return `This menu shows ${String(shown)} of ${String(reported)} matching values. Type more of the name to reach the rest.`;
}

/**
 * The placeholder in a facet menu's search box.
 *
 * Names neither the menu nor the source: a fragment reaches the source's own list for a facet it
 * searches and the menu's rows for one it does not, and the box is the same box either way.
 */
export const FACET_MENU_SEARCH = "Search values";

/** What a facet menu reads when nothing it holds matches, its values having not been looked up. */
export const FACET_MENU_NO_MATCHES = "No values in this menu match.";

/** What a facet menu reads while the source is being asked for the values that match. */
export const FACET_VALUES_ASKING = "Looking for matching values.";

/** What a facet menu reads when the source answered and matched nothing. */
export const FACET_VALUES_NONE_MATCH = "The metadata source lists no value matching this.";

/**
 * What a facet menu reads when the lookup did not answer.
 *
 * The second sentence is the point of the message. Rows that simply did not appear would read as a
 * source that lists no such value, which is the answer this read never got.
 */
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

/**
 * What heads the selection popover, and the name the panel announces.
 *
 * Reuses {@link selectionCount}, so the count grammar is the one the selection bar already pins.
 */
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
 * Built from the two consequence sentences declared above rather than from prose of its own, so the
 * cost a reader is warned about is worded once.
 *
 * @param count how many entities the choice covers
 * @param oneWayDoor whether a later scope change leaves what the wider scope already made wanted
 */
export function allScenesConfirmation(count: number, oneWayDoor: boolean): string {
  return [
    entitiesCovered(count),
    ALL_SCENES_MARKS_THE_BACK_CATALOGUE,
    ...(oneWayDoor ? [ALL_SCENES_IS_NOT_UNDONE_BY_A_LATER_SCOPE_CHANGE] : []),
  ].join(" ");
}

/**
 * What marking a catalogue does not do, stated where the whole catalogue is about to be marked.
 *
 * The reason the confirmation exists. The gesture reaches every scene the source lists, which reads
 * like a download of that size unless the sentence says otherwise.
 */
export const MONITOR_ALL_DOWNLOADS_NOTHING_BY_ITSELF =
  "Marking a scene wanted downloads nothing by itself.";

/**
 * What the confirmation in front of the whole catalogue states.
 *
 * Names the catalogue's own size, which is the figure the count line beside the grid states, and
 * says in the same breath that the scenes already held are not part of the run. The two together are
 * what stop the figure reading as the number of scenes about to be registered.
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

/**
 * A refresh that failed over content already on screen.
 *
 * Says what is on screen rather than what went wrong: the values are still the last true answer, and
 * what the reader needs to know is that they may have moved since.
 */
export const READ_IS_STALE =
  "Cove couldn't check this just now. These are the last values it read.";

/** No Cove library folder holds the reported file at all. */
export const IMPORT_CAUSE_NOT_FOUND = "No Cove library folder holds this file.";

/** The reported name is under more than one library folder, so none was chosen. */
export const IMPORT_CAUSE_AMBIGUOUS =
  "This name is under more than one of your library folders, so Cove did not choose between them.";

/** The file was found where it was reported and Cove's own import would not take it. */
export const IMPORT_CAUSE_UNREADABLE = "Cove found this file and would not take it in.";

/**
 * One Whisparr root folder's outstanding refusals.
 *
 * Names the root, so the reader has the folder to go and look at rather than a total.
 */
export function importRefusalsUnderRootSentence(root: string, count: number): string {
  return `${String(count)} ${count === 1 ? "file" : "files"} under ${root} ${count === 1 ? "has" : "have"} not reached your library since an import from it last worked.`;
}

/**
 * Refusals Whisparr reported under none of its own root folders.
 *
 * The stored aggregate keys these under a blank root, which is not a sentence, so this is what the
 * reader is shown in its place.
 */
export function importRefusalsWithNoReportedRootSentence(count: number): string {
  return `${String(count)} ${count === 1 ? "file" : "files"} ${count === 1 ? "has" : "have"} not reached your library, and Whisparr reported ${count === 1 ? "it" : "them"} under none of its own root folders.`;
}

/**
 * Files Whisparr reported that Cove's catch-up could not take, and has already moved past.
 *
 * The catch-up keeps one mark and nothing moves it back, so these records are beyond it for good.
 * `when` is the rendered instant, or null while none is recorded.
 */
export function importsPassedOverSentence(count: number, when: string | null): string {
  const files = count === 1 ? "file" : "files";
  const them = count === 1 ? "it" : "them";
  const passedOver = `${String(count)} ${files} Whisparr reported could not be taken in, and Cove's regular catch-up has moved past ${them}, so it will not try ${them} again.`;
  return when === null ? passedOver : `${passedOver} Most recently ${when}.`;
}

/**
 * What the default upgrade behaviour does, in the terms the reader sees the result in.
 *
 * Neither behaviour removes anything from disk, so both sentences say so rather than leaving the
 * reader to infer it from the one that mentions it.
 */
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
 *
 * Names the version found, and names the other application from the value that instance actually
 * sent rather than from a table of applications this code knows about. Offers no retry and advises
 * no setting: neither would change the answer.
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
