/**
 * The settings panel's vocabulary for the saved rename settings.
 *
 * The settings themselves are the generated wire contract. The backend owns the defaults, the stored
 * spelling, the one-time conversions and what an unreadable blob falls back to; this panel edits what
 * `GET /options` hands it and sends the result back. Per-member documentation stays on the C# records,
 * which state each rule more fully than a mirrored copy could.
 */

import type * as Wire from "../wire/api";

/**
 * A generated wire shape with the optional markers dropped.
 *
 * The generator marks every member of a C# record with a default initializer optional, because a
 * request may omit it. A response never does - the server writes all of them - and a panel control
 * needs a value, so the response side is modeled as complete. A backend test holds the server to it.
 */
type Complete<T> = T extends readonly (infer E)[]
  ? Complete<E>[]
  : T extends object
    ? { [K in keyof T]-?: Complete<T[K]> }
    : T;

export type RenamerOptions = Complete<Wire.RenamerOptions>;
export type OptionsView = Complete<Wire.OptionsView>;
export type MultiValueOptions = Complete<Wire.MultiValueOptions>;
export type Destination = Complete<Wire.Destination>;
export type PathDestinationRule = Complete<Wire.PathDestinationRule>;
export type ExcludeRule = Complete<Wire.ExcludeRule>;
export type FieldReplaceRule = Complete<Wire.FieldReplaceRule>;
export type KindOptions = Complete<Wire.KindOptions>;
export type CaseTransform = Wire.CaseTransform;
export type OverflowPolicy = Wire.OverflowPolicy;
export type SortOrder = Wire.SortOrder;

/**
 * The entity kinds this extension renames, which is fewer than the kinds Cove has. Typed as a subset
 * of the wire spelling, so a key this panel cannot index the per-kind map with fails the type-check.
 */
export const RENAMABLE_KINDS = [
  "video",
  "image",
  "audio",
  "text",
] as const satisfies readonly Wire.RenamerFileKind[];

export type RenamableKind = (typeof RENAMABLE_KINDS)[number];

/** How each kind is named in the panel. */
export const KIND_LABELS: Readonly<Record<RenamableKind, string>> = {
  video: "Videos",
  image: "Images",
  audio: "Audio",
  text: "Text documents",
};

/**
 * The stored root standing for _the file's own library path_ - an empty string, so that a rule
 * carries no copy of a path Cove owns.
 */
export const CONTAINING_ROOT = "";

/**
 * How {@link CONTAINING_ROOT} is offered in a picker. User-facing copy the docs name too, so the two
 * editors showing it must not be able to word it differently.
 */
export const CONTAINING_ROOT_LABEL = "(the file's own library path)";

/** A destination naming neither a root nor a folder: the state that moves nothing. */
export const NO_DESTINATION: Destination = { root: CONTAINING_ROOT, template: "" };

// Trailing separators are trimmed by index rather than by a `/+$` regex. That pattern is anchored at
// the end but searched from the front, so a path of N separators costs O(N^2) backtracking - a
// polynomial-ReDoS shape, and the path reaching here is host data rather than anything this panel
// authored. Walking back from the end is linear and produces the same key.
const sameFolderKey = (path: string) => {
  const forward = path.replaceAll("\\", "/");
  let end = forward.length;
  while (end > 0 && forward[end - 1] === "/") end -= 1;
  return forward.slice(0, end);
};

/**
 * Cove's library path that `root` names, or `undefined` when it names none - which is the state that
 * skips the rule, so it is the state the editors badge.
 *
 * Only the separator style and a trailing separator are forgiven, because a root written by the
 * one-time conversion is normalized while one Cove hands back carries the platform's own spelling.
 * Case is deliberately not forgiven: a converted root is the library path's own casing and a picked
 * one is the string the endpoint gave, so folding case would be this panel inventing a second
 * opinion about when two paths name one folder, on a host whose case rule it cannot see.
 */
export function chosenLibraryPath(
  root: string,
  libraryPaths: readonly string[],
): string | undefined {
  const wanted = sameFolderKey(root);
  return libraryPaths.find((path) => sameFolderKey(path) === wanted);
}

/**
 * Cove's library paths as the panel currently knows them - which is not always as a list.
 *
 * The three states are kept apart because an empty list means three different things and the panel
 * says something different about each: not read yet, read and failed, read and genuinely none. A hook
 * returning the list alone collapses all three onto the last one.
 */
export interface LibraryPathsState {
  readonly paths: readonly string[];
  /** True until the read settles, either way. */
  readonly loading: boolean;
  /** True when the read settled by failing, in which case `paths` is empty and means nothing. */
  readonly failed: boolean;
}

/**
 * What a destination editor may say about the library paths themselves.
 *
 * `"unreadable"` and `"no-library-paths"` are deliberately not one value: only the second names a
 * repair, and offering it after a failed read tells a user to do something they have already done.
 */
export type DestinationNotice = "none" | "unreadable" | "no-library-paths";

/** What a destination editor draws for one stored root. */
export interface DestinationPickerState {
  /** The library path `root` names, or `undefined` when it names none. */
  readonly chosen: string | undefined;
  /** The stored root is not one of Cove's library paths, so the rule is skipped. */
  readonly stale: boolean;
  readonly showPicker: boolean;
  readonly notice: DestinationNotice;
}

/**
 * The one derivation both destination surfaces draw from - which library path a stored root names,
 * whether that root has stopped being one, and whether the control for changing it is on screen.
 *
 * Written once because the two surfaces must not be able to disagree about the same root. Nothing is
 * badged until the read has settled successfully: an unsettled read carries an empty list, and
 * reading that as "the host has no library paths" badges every rule broken on every page mount, which
 * sends the user to re-pick destinations that were working - and re-picking moves real files.
 */
export function destinationPicker(
  root: string,
  library: LibraryPathsState,
): DestinationPickerState {
  const known = !library.loading && !library.failed;
  const chosen = chosenLibraryPath(root, library.paths);
  const stale = known && root !== CONTAINING_ROOT && chosen === undefined;

  let notice: DestinationNotice = "none";
  if (library.failed) notice = "unreadable";
  else if (known && library.paths.length === 0) notice = "no-library-paths";

  // The picker comes back for a stale root even with nothing to pick, because that is the state that
  // stops the rule working: hiding it then would leave the user reading a skip reason with no way to
  // act on it.
  return { chosen, stale, showPicker: library.paths.length > 0 || stale, notice };
}
