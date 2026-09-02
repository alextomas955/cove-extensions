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
