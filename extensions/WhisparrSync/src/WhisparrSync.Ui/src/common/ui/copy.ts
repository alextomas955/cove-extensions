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
 * The entity control's own name while the entity is not monitored in Whisparr.
 *
 * The control carries the product's mark instead of a word, so its accessible name is the only name
 * it has. A filled two-tone disc cannot inherit `currentColor`, so it cannot carry the state either.
 */
export const MONITOR_IN_WHISPARR = "Monitor in Whisparr";

/** The same control's name once the connected instance monitors the entity. */
export const MONITORED_IN_WHISPARR = "Monitored in Whisparr";

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
 * The narrower monitor scope, in Whisparr's own words.
 *
 * Both generations use these two names, so neither is this product's coinage and neither has to be
 * reconciled across a generation. Whisparr's other monitor wording is not carried across: its own
 * dropdown renders unsubstituted localization keys, so mimicry stops here.
 */
export const SCOPE_FUTURE_SCENES = "Future Scenes";

/** The wider monitor scope, in Whisparr's own words. */
export const SCOPE_ALL_SCENES = "All Scenes";

/**
 * What the wider scope costs, stated where the scope is chosen rather than after it is taken.
 *
 * Names the cost the reader pays rather than the flag that is written, because a flag is not
 * something anyone budgets for.
 */
export const ALL_SCENES_MARKS_THE_BACK_CATALOGUE =
  "All Scenes marks every scene Whisparr already lists for this entity as wanted, which spends indexer traffic and disk.";

/**
 * What choosing a scope does not decide, stated beside every scope option.
 *
 * Naming only the wider scope's cost would leave the narrower one reading as a limit. The connected
 * instance decides what a scope covers and has been seen covering everything, so the reader is told
 * that rather than being left to infer a guarantee.
 *
 * The wording here is provisional and single-sourced so it is one edit to change.
 */
export const SCOPE_DOES_NOT_LIMIT_WHAT_IS_MONITORED =
  "Whisparr monitors every scene it lists for this entity whichever scope you choose.";

/**
 * That the wider scope is a one-way door, stated where the scope is chosen.
 *
 * For the generation whose date gate applies only to what a later refresh adds. The generation that
 * rewrites every flag on a scope change does not render this, because there it would be false.
 */
export const ALL_SCENES_IS_NOT_UNDONE_BY_A_LATER_SCOPE_CHANGE =
  "Changing the scope back to Future Scenes does not undo this: a scene that is already wanted stays wanted.";

/**
 * What unmonitoring stops and what it leaves behind.
 *
 * The second sentence is the point of the message. A reader who unmonitors to stop acquisition has
 * not stopped it, and no other sentence in this product says so.
 */
export const UNMONITORING_DOES_NOT_RETRACT =
  "Unmonitoring stops Whisparr wanting new scenes. It does not retract what All Scenes already made wanted.";

/**
 * Why a performer is offered no scope choice, stated on the one item that replaces the pair.
 *
 * Monitoring a performer with no date gate is All-Scenes behaviour, so the item says so rather than
 * presenting the wider scope as the only option.
 */
export const PERFORMER_HAS_NO_FUTURE_ONLY_SCOPE =
  "Whisparr offers no future-only option for a performer, so monitoring one covers every scene it lists.";

/**
 * What reflect owned does, for both generations at once.
 *
 * Neither generation offers an in-place import mode, so a sentence promising one generation less
 * than the other could not be written truthfully.
 */
export const REFLECT_OWNED =
  "Whisparr links each file you already own into its scene's folder. This costs no extra disk while Whisparr's hard-link setting is on, and is skipped while that setting is off.";

/** Why nothing was linked. Names the setting, because turning it on is what changes the answer. */
export const REFLECT_OWNED_SKIPPED =
  "Skipped: with Whisparr's hard-link setting off each file would be copied rather than linked, and would use disk twice.";

/** What add all missing does. Says what it does not do, because the name suggests acquisition. */
export const ADD_ALL_MISSING =
  "Registers every scene Cove holds that Whisparr does not. Nothing is downloaded.";

/** The one action that downloads, said plainly rather than shaded into the others. */
export const SEARCH_ALL_MONITORED =
  "Asks Whisparr to search for every scene it wants for this entity, and to download what it finds.";

/** The item that turns monitoring off. Named for what it does, not for the flag it writes. */
export const STOP_MONITORING_IN_WHISPARR = "Stop monitoring in Whisparr";

/**
 * What the selection overlay asks, above the actions it offers.
 *
 * Says "every" rather than naming a count. The count is on screen in the selection bar the reader
 * just used, and a second copy of it here would be a second thing that can be wrong.
 */
export const BULK_CHOOSE_AN_ACTION = "Choose what to do with every entity you selected.";

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

/** The overlay's way out, on the choice it presents. */
export const BULK_CANCEL = "Cancel";

/** The overlay's way out when there is nothing to choose between. */
export const BULK_CLOSE = "Close";

/** The three secondary actions, named as the product names them everywhere it offers them. */
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

/** Whisparr's own renaming reaching files Cove already holds. */
export const WHISPARR_MAY_RENAME = "Whisparr may change files in your library";

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
