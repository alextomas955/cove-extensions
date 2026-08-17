/**
 * The settings panel's view of `src/Renamer/Options/RenamerOptions.cs`.
 *
 * The shapes below are a mechanical re-casing of the generated wire contract rather than a hand
 * transcription of it: `Pascal<>` derives each one from `../wire/api`, so a change to the C# record
 * reaches this panel through the committed document. Per-member documentation lives on the C#
 * records they derive from, which state each rule more fully than a mirrored copy could.
 *
 * The PascalCase spelling is retained because it is the spelling of the PERSISTED options blob on
 * every existing installation, not because it documents anything: `MODELED_KEYS` is built from
 * `DEFAULT_OPTIONS`' runtime keys, and the C# `OptionsStore` writes the same PascalCase. Re-casing
 * here would make every stored key look unmodeled and carry it through beside a new camelCase twin,
 * and would leave the two writers disagreeing about the blob's spelling forever. The enums
 * serialize as STRINGS (C# `JsonStringEnumConverter`), so their VALUES are re-cased too — "None",
 * "DropAll" and the rest are wire values in their own right.
 *
 * DEFAULT_OPTIONS reproduces the C# record's default initializers verbatim, so a first-run
 * panel (no stored "options" blob) shows the same defaults the backend would apply.
 */
import type * as Wire from "../wire/api";

/**
 * Re-cases a generated wire shape into the persisted PascalCase spelling: object keys and
 * string-enum literals are capitalized, a free-form `string` is left alone, numbers, booleans,
 * `null` and `undefined` pass through unchanged, and an array's element type is mapped. `-?` drops
 * the optional markers the generator emits for a C# property with a default initializer, which the
 * panel always supplies from {@link DEFAULT_OPTIONS}.
 */
type Pascal<T> = T extends string
  ? string extends T
    ? T
    : Capitalize<T>
  : T extends number | boolean | null | undefined
    ? T
    : T extends readonly (infer E)[]
      ? Pascal<E>[]
      : { [K in keyof T as K extends string ? Capitalize<K> : K]-?: Pascal<T[K]> };

export type CaseTransform = Pascal<Wire.CaseTransform>;
export type OverflowPolicy = Pascal<Wire.OverflowPolicy>;
export type SortOrder = Pascal<Wire.SortOrder>;
export type MultiValueOptions = Pascal<Wire.MultiValueOptions>;
export type PathDestinationRule = Pascal<Wire.PathDestinationRule>;
export type Destination = Pascal<Wire.Destination>;

/**
 * The stored root standing for _the file's own library path_ — an empty string, so that a rule
 * carries no copy of a path Cove owns. Declared here because the helpers below are what reason about
 * it, and it had grown three spellings across the two editors that read it.
 */
export const CONTAINING_ROOT = "";

/**
 * How {@link CONTAINING_ROOT} is offered in a picker. User-facing copy that the docs name too, so the
 * two editors showing it must not be able to word it differently.
 */
export const CONTAINING_ROOT_LABEL = "(the file's own library path)";

/** A destination naming neither a root nor a folder: the state that moves nothing. */
export const NO_DESTINATION: Destination = { Root: CONTAINING_ROOT, Template: "" };

/**
 * Cove's library path that `root` names, or `undefined` when it names none — which is the state that
 * skips the rule, so it is the state the editors badge.
 *
 * Exact equality would do for anything this panel wrote, because a root is only ever stored by
 * copying a string out of `libraryPaths`, and the backend now emits one canonical spelling of each
 * (forward slashes, no trailing separator — `CoveRenamerDataPort.ReadLibraryRoots`). What exact
 * equality does NOT survive is a root stored BEFORE that: the endpoint used to hand back Cove's own
 * platform spelling, so a store written by the old panel holds `I:\Downloads\P` where the one-time
 * conversion wrote `I:/Downloads/P`. Both name one folder and the planner treats them as one, so
 * badging such a rule broken would send a user to re-pick a destination that already works.
 *
 * Only the separator and a trailing one are forgiven. Case is deliberately not, because a case
 * difference cannot arise: a converted root is the library path's own casing (it IS the library
 * path), and a picked one is the string the endpoint gave. Folding case here would be this panel
 * inventing a second opinion about when two paths are one folder, on a host whose case rule it
 * cannot see.
 */
export function chosenLibraryPath(
  root: string,
  libraryPaths: readonly string[],
): string | undefined {
  const wanted = sameFolderKey(root);
  return libraryPaths.find((path) => sameFolderKey(path) === wanted);
}

// Trailing separators are trimmed by index rather than by a `/+$` regex. That pattern is anchored at
// the end but searched from the front, so a path of N separators costs O(N^2) backtracking — a
// polynomial-ReDoS shape, and the path reaching here is host data rather than anything this panel
// authored. Walking back from the end is linear and produces the same key.
const sameFolderKey = (path: string) => {
  const forward = path.replace(/\\/g, "/");
  let end = forward.length;
  while (end > 0 && forward[end - 1] === "/") end -= 1;
  return forward.slice(0, end);
};

/**
 * The root a destination stores when Cove has exactly ONE library path: that path itself, never the
 * _(the file's own library path)_ sentinel.
 *
 * With one library path the two resolve to the same folder for every file already inside the library,
 * so the choice is visible only for a file outside it — the sentinel cannot place such a file and skips
 * it, where a named root takes it in. A rule NAMES a destination, and items it matches go there
 * wherever they are sitting now; storing the sentinel where a real path exists would be this panel
 * quietly answering the other question.
 *
 * Returns the sentinel for none (there is nothing to name) and for several (the picker is shown and the
 * user names one).
 */
export function soleLibraryPath(libraryPaths: readonly string[]): string {
  return libraryPaths.length === 1 ? (libraryPaths[0] ?? CONTAINING_ROOT) : CONTAINING_ROOT;
}

/**
 * A brand-new destination rule, as this panel's *add* buttons create one.
 *
 * Applied at CREATION only, never to a value already stored: a rule on disk holding the sentinel is a
 * destination the user has, and rewriting it on load would relocate their files with no edit made.
 */
export function newDestination(libraryPaths: readonly string[]): Destination {
  return { Root: soleLibraryPath(libraryPaths), Template: "" };
}

/**
 * The root *Where files go* stores — the same auto-selection, with the one carve-out the default needs.
 *
 * A rule exists because someone created it, so it names a place even with a blank template. The
 * default's blank template is instead the shipped "rename in place, nothing moves" state. A named root
 * always relocates — that is what picking a folder out of a list means — so auto-selecting the sole
 * library path against a blank template would turn the shipped default into "move every unmatched file
 * to the top of the library", a destination nobody chose. The sole path is therefore stored exactly
 * when the default names a folder to make.
 *
 * A stored root is returned untouched whenever the picker is on screen and the value is the user's own
 * answer: several library paths, none at all, or a root that is no longer one of them.
 */
export function defaultDestinationRoot(
  folderTemplate: string,
  storedRoot: string,
  libraryPaths: readonly string[],
): string {
  const sole = soleLibraryPath(libraryPaths);
  if (sole === CONTAINING_ROOT) return storedRoot;
  if (storedRoot !== CONTAINING_ROOT && chosenLibraryPath(storedRoot, libraryPaths) !== sole) {
    return storedRoot;
  }
  // A BLANK folder template, not the sentinel — the shipped "rename in place, nothing moves" state.
  return folderTemplate === "" ? CONTAINING_ROOT : sole;
}

/**
 * Cove's library paths as the panel currently knows them — which is not always as a list.
 *
 * The three states are kept apart because an empty list means three different things and the panel
 * says something different about each: not read yet, read and failed, read and genuinely none. A hook
 * returning the list alone collapses all three onto the last one, which is the false alarm the
 * destination model exists to remove.
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
 * The one derivation both destination surfaces draw from — which library path a stored root names,
 * whether that root has stopped being one, and whether the control for changing it is on screen.
 *
 * Written once because the two surfaces must not be able to disagree about the same root; they were
 * written twice and had already drifted. The picker is hidden when there is nothing to pick — one
 * library path is the whole library, so asking which to use is a question with one answer, and
 * {@link newDestination} has already stored it. It comes BACK when the stored root is not among the
 * current paths, which is the state that stops the rule working: hiding it then would leave the user
 * reading a skip reason with no way to act on it.
 *
 * Nothing is badged until the read has SETTLED successfully. An unsettled read carries an empty list,
 * and reading that as "the host has no library paths" badges every rule broken on every page mount —
 * a false alarm which is worse than a real one, because the user re-picks destinations that were
 * working and re-picking moves real files.
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

  return { chosen, stale, showPicker: library.paths.length > 1 || stale, notice };
}

export type ExcludeRule = Pascal<Wire.ExcludeRule>;
export type FieldReplaceRule = Pascal<Wire.FieldReplaceRule>;

/**
 * All rename settings this panel edits, derived from the contract with two deliberate departures.
 *
 * `FreeSpaceHeadroomBytes` is omitted: it is the one C# knob the panel never edits, and leaving it
 * out keeps it out of `MODELED_KEYS`, which is what lets {@link extractUnmodeledFields} carry a
 * stored value through a load → save round trip untouched instead of {@link normalizeOptions}
 * consuming and dropping it. Modeling it here would start writing it and move a persisted byte.
 *
 * The three `Enable*` gates have no C# counterpart at all — they are panel state that rides along
 * in the same stored blob, which the backend ignores as an unknown property. They cannot be derived
 * because the contract does not describe them.
 *
 * `StudioDestinations`/`TagDestinations` derive to a string-keyed index signature, because a
 * `Dictionary<int, Destination>` generates as one and JSON object keys are strings either way. Their
 * keys are still stable entity ids and never names — a rename in Cove must not orphan a rule or
 * split one entity across two destination trees — and the coercions on both sides of this module
 * (`destinationMap` here, `fromStringKeyed` in the editors) are what keep them integral.
 */
type DerivedOptions = Omit<Pascal<Wire.RenamerOptions>, "FreeSpaceHeadroomBytes">;

export interface RenamerOptions extends DerivedOptions {
  EnableStudioDestinations: boolean;
  EnableTagDestinations: boolean;
  EnableAdvancedRouting: boolean;
}

/**
 * The C# defaults (RenamerOptions.cs):
 *   FilenameTemplate "{$date - }$title{ [$resolution]}", FolderTemplate "", DateFormat "yyyy-MM-dd",
 *   DurationFormat verbatim `hh\-mm\-ss`, Performers.Separator " ", Tags.Separator " ",
 *   FilenameMax 255, FullPathMax 259, the 9-field DropOrder, RequiredFields ["title"],
 *   DuplicateSuffixFormat " ({n})", Articles ["The","A","An"], PreventConsecutiveSegments true,
 *   FilenameAsTitle off and RemoveEmptyFolder off (both write beyond a name — one records a title on
 *   the item, one deletes a folder — so both stay opt-in), every routing map {} and every routing or
 *   exclude list [].
 *
 * The list stops there, and its silence is not a claim: a default it does not name is not thereby off
 * or empty — read RenamerOptions.cs for that one. Every value restated here is a second copy that can
 * fall out of step, which is why the list names only the non-obvious ones and no catch-all closes it.
 *
 * Both separators are " " because the effective default is the RenamerOptions initializer, which
 * constructs each multi-value field as new() { Separator = " " }. MultiValueOptions declares its own
 * separator and this overrides it, so the nested class default is never the value in force: read a
 * default off the record's initializer, never off the class underneath it.
 *
 * CrossVolumeConcurrency / SameVolumeConcurrency ARE modeled — the Advanced panel edits them, so
 * they belong in DEFAULT_OPTIONS (and therefore MODELED_KEYS + normalizeOptions). FreeSpaceHeadroomBytes
 * is the ONE remaining knob the panel never edits; the block above DerivedOptions states in full why
 * omitting it is what carries a stored value through a load → save round-trip untouched.
 *
 * Nothing enforces this mirroring. The generated wire contract carries these property shapes but no
 * defaults, so a value that drifts from the record shows a new user a wrong default in the panel and
 * fails no check; emitting the values from the contract is what would remove the copy.
 */
export const DEFAULT_OPTIONS: RenamerOptions = {
  FilenameTemplate: "{$date - }$title{ [$resolution]}",
  FolderTemplate: "",
  FolderRoot: "",
  DateFormat: "yyyy-MM-dd",
  // C# verbatim string @"hh\-mm\-ss" → the literal value contains single backslashes.
  DurationFormat: "hh\\-mm\\-ss",
  Performers: {
    Separator: " ",
    MaxCount: 0,
    OnOverflow: "DropAll",
    Sort: "NameAsc",
    WhitelistIds: [],
    BlacklistIds: [],
    IgnoreGenders: [],
    GenderOrder: [],
  },
  Tags: {
    Separator: " ",
    MaxCount: 0,
    OnOverflow: "DropAll",
    Sort: "NameAsc",
    WhitelistIds: [],
    BlacklistIds: [],
    IgnoreGenders: [],
    GenderOrder: [],
  },
  IllegalReplacement: "",
  SpaceReplacement: "",
  RemoveCharacters: ",#",
  Case: "None",
  AsciiTransliterate: false,
  NormalizePunctuation: true,
  FilenameMax: 255,
  FullPathMax: 259,
  CrossVolumeConcurrency: 2,
  SameVolumeConcurrency: 8,
  DropOrder: [
    "videoCodec",
    "audioCodec",
    "frameRate",
    "resolution",
    "tags",
    "studioCode",
    "studio",
    "performers",
    "date",
  ],
  OnlyOrganized: false,
  FilenameAsTitle: false,
  RequiredFields: ["title"],
  DuplicateSuffixFormat: " ({n})",
  AutoRenamerOnUpdate: false,
  StudioDestinations: {},
  TagDestinations: {},
  PathDestinations: [],
  ExcludeTagIds: [],
  ExcludeStudioIds: [],
  ExcludePaths: [],
  AllowedRoots: [],
  AssociatedExtensions: [],
  UnorganizedDestination: null,
  EnableStudioDestinations: false,
  EnableTagDestinations: false,
  EnableAdvancedRouting: false,
  RemoveEmptyFolder: false,
  SqueezeStudioNames: false,
  FieldReplacers: [],
  StripLeadingArticles: false,
  Articles: ["The", "A", "An"],
  PreventTitlePerformer: false,
  PreventConsecutiveSegments: true,
};

/**
 * Deep clone of DEFAULT_OPTIONS so callers can mutate form state without touching the const.
 * Every mutable member (the multi-value lists, the routing maps, and the rule/path arrays) is
 * fresh-copied; a missed member would let one form instance mutate the shared default for the next.
 */
export function cloneDefaults(): RenamerOptions {
  return {
    ...DEFAULT_OPTIONS,
    Performers: {
      ...DEFAULT_OPTIONS.Performers,
      WhitelistIds: [],
      BlacklistIds: [],
      IgnoreGenders: [],
      GenderOrder: [],
    },
    Tags: {
      ...DEFAULT_OPTIONS.Tags,
      WhitelistIds: [],
      BlacklistIds: [],
      IgnoreGenders: [],
      GenderOrder: [],
    },
    DropOrder: [...DEFAULT_OPTIONS.DropOrder],
    RequiredFields: [...DEFAULT_OPTIONS.RequiredFields],
    StudioDestinations: { ...DEFAULT_OPTIONS.StudioDestinations },
    TagDestinations: { ...DEFAULT_OPTIONS.TagDestinations },
    PathDestinations: DEFAULT_OPTIONS.PathDestinations.map((r) => ({ ...r, Dest: { ...r.Dest } })),
    ExcludeTagIds: [...DEFAULT_OPTIONS.ExcludeTagIds],
    ExcludeStudioIds: [...DEFAULT_OPTIONS.ExcludeStudioIds],
    ExcludePaths: DEFAULT_OPTIONS.ExcludePaths.map((r) => ({ ...r })),
    AllowedRoots: [...DEFAULT_OPTIONS.AllowedRoots],
    AssociatedExtensions: [...DEFAULT_OPTIONS.AssociatedExtensions],
    FieldReplacers: DEFAULT_OPTIONS.FieldReplacers.map((r) => ({ ...r })),
    Articles: [...DEFAULT_OPTIONS.Articles],
  };
}

// ── normalizeOptions: the /preview-sample dual-source fix ──
//
// A legacy stored "options" blob can carry STALE camelCase duplicate keys (e.g. `filenameTemplate`,
// `dateFormat`) alongside the canonical PascalCase keys. The old load path spread-merged the raw blob,
// so those stale keys rode into the /preview-sample request body AFTER the live PascalCase ones; the
// backend binds case-insensitively with default last-write-wins, so the stale value overwrote the live
// edit and the preview never changed. normalizeOptions rebuilds a clean, fully-canonical RenamerOptions
// from cloneDefaults() reading ONLY the known PascalCase keys (coerced by declared type), DROPPING every
// unknown/stale key. Applied at the load boundary, it fixes the preview AND self-heals the stored blob on
// the next Save (since the canonical state is what gets persisted). Frontend-only; no backend change.
//
// The six id-keyed fields (both groups' WhitelistIds/BlacklistIds, TagDestinations, ExcludeTagIds) read
// through the numeric coercers, so a blob still holding the pre-migration NAMES coerces to an empty list
// or map rather than surviving as unusable strings. Holding the old names in a parallel field here would
// re-introduce the duplicate state the backend's one-time name→id conversion exists to remove — so the
// panel keeps that erasure and instead refuses to SAVE while any name still awaits conversion
// (hasUnmigratedNameRules below). A refusal is what the backend does when it cannot resolve, which is
// precisely the state in which a save from here would persist these emptied fields over rules the
// conversion has not applied yet.

function asRecord(v: unknown): Record<string, unknown> {
  return v && typeof v === "object" ? (v as Record<string, unknown>) : {};
}
function str(v: unknown, fallback: string): string {
  return typeof v === "string" ? v : fallback;
}
function num(v: unknown, fallback: number): number {
  return typeof v === "number" && Number.isFinite(v) ? v : fallback;
}
function bool(v: unknown, fallback: boolean): boolean {
  return typeof v === "boolean" ? v : fallback;
}
function strArray(v: unknown, fallback: string[]): string[] {
  return Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : fallback;
}
function numArray(v: unknown, fallback: number[]): number[] {
  return Array.isArray(v)
    ? v.filter((x): x is number => typeof x === "number" && Number.isFinite(x))
    : fallback;
}
/**
 * Coerce one stored destination. A blob written before this release holds a bare STRING here — a typed
 * absolute root — which the backend's one-time conversion rewrites into a root/template pair. Until it
 * has run, this panel must not present that string as either half of the pair: showing it as a root
 * would offer a path that is not in the picker's list, and showing it as a template would silently
 * turn a destination into a folder name. It coerces to "unset", and the backend's own conversion is
 * what recovers it.
 */
function destination(v: unknown): Destination | null {
  if (!v || typeof v !== "object" || Array.isArray(v)) return null;
  const r = v as Record<string, unknown>;
  return { Root: str(r.Root, ""), Template: str(r.Template, "") };
}
// A routing map can arrive from a hand-edited/legacy blob with malformed values or non-numeric keys
// (every routing map is id-keyed). Keep only the entries that conform and rebuild a fresh plain
// object, so a malformed map yields a safe shape rather than propagating bad data.
function destinationMap(v: unknown): Record<number, Destination> {
  const src = asRecord(v);
  const out: Record<number, Destination> = {};
  for (const [k, val] of Object.entries(src)) {
    const n = Number(k);
    const d = destination(val);
    if (Number.isInteger(n) && d) out[n] = d;
  }
  return out;
}
// The key codec between a persisted destination map and the string-keyed map the reusable
// `KeyValueMapEditor` works in. It lives beside `numKeyStringMap` because `fromStringKeyed` has to
// agree with it entry for entry: a destination rule keys on the entity's stable id, and that id must
// stay a NUMBER end to end so the persisted map is value-equal with the backend
// `Record<number, string>`. Two files was one file too many for a rule that has to hold in both.

/**
 * Adapt a number-keyed destination map to the string-keyed shape `KeyValueMapEditor` consumes. JS
 * object keys are strings regardless, so this is the explicit, typed crossing of that boundary rather
 * than a silent cast.
 */
export function toStringKeyed(map: Record<number, Destination>): Record<string, Destination> {
  const out: Record<string, Destination> = {};
  for (const [k, v] of Object.entries(map)) out[k] = v;
  return out;
}

/**
 * Adapt the editor's string-keyed map back to the backend `Record<number, string>`.
 *
 * Mirrors {@link numKeyStringMap} above: keep only entries whose key is an integer and whose value is
 * a string, rebuilding a fresh plain object. A hand-edited/legacy blob can carry a non-integer key
 * ("x", "1.5") — dropping it here yields a safe shape value-equal with that coercion rather than
 * propagating a NaN key downstream.
 */
export function fromStringKeyed(map: Record<string, Destination>): Record<number, Destination> {
  const out: Record<number, Destination> = {};
  for (const [k, v] of Object.entries(map)) {
    const n = Number(k);
    const d = destination(v);
    if (Number.isInteger(n) && d) out[n] = d;
  }
  return out;
}

function pathDestinations(v: unknown): PathDestinationRule[] {
  return Array.isArray(v)
    ? v
        .filter((x) => x && typeof x === "object")
        .map((x) => {
          const r = x as Record<string, unknown>;
          return {
            Pattern: str(r.Pattern, ""),
            Dest: destination(r.Dest) ?? NO_DESTINATION,
            IsRegex: bool(r.IsRegex, false),
          };
        })
    : [];
}
function excludeRules(v: unknown): ExcludeRule[] {
  return Array.isArray(v)
    ? v
        .filter((x) => x && typeof x === "object")
        .map((x) => {
          const r = x as Record<string, unknown>;
          return { Pattern: str(r.Pattern, ""), IsRegex: bool(r.IsRegex, false) };
        })
    : [];
}
function fieldReplacers(v: unknown): FieldReplaceRule[] {
  return Array.isArray(v)
    ? v
        .filter((x) => x && typeof x === "object")
        .map((x) => {
          const r = x as Record<string, unknown>;
          return {
            TargetToken: str(r.TargetToken, ""),
            Find: str(r.Find, ""),
            Replace: str(r.Replace, ""),
          };
        })
    : [];
}
function overflow(v: unknown): OverflowPolicy {
  return v === "KeepFirst" ? "KeepFirst" : "DropAll";
}
function sortOrder(v: unknown): SortOrder {
  if (v === "None" || v === "IdAsc" || v === "FavoriteFirst") return v;
  return "NameAsc";
}
function caseTransform(v: unknown): CaseTransform {
  return v === "Lower" || v === "Title" ? v : "None";
}
function normalizeMultiValue(raw: unknown, def: MultiValueOptions): MultiValueOptions {
  const r = asRecord(raw);
  return {
    Separator: str(r.Separator, def.Separator),
    MaxCount: num(r.MaxCount, def.MaxCount),
    OnOverflow: overflow(r.OnOverflow),
    Sort: sortOrder(r.Sort),
    WhitelistIds: numArray(r.WhitelistIds, []),
    BlacklistIds: numArray(r.BlacklistIds, []),
    IgnoreGenders: strArray(r.IgnoreGenders, []),
    GenderOrder: strArray(r.GenderOrder, []),
  };
}

/**
 * The top-level keys this panel models. Any other key in a stored blob belongs to a backend-only
 * option (e.g. the path-routing fields configured outside this panel) and must be carried through a
 * load → save round-trip untouched rather than dropped.
 */
const MODELED_KEYS: ReadonlySet<string> = new Set(Object.keys(DEFAULT_OPTIONS));

/**
 * Extract the stored keys this panel does not model, so a Save can merge them back and never erase
 * backend-only settings. Returns an empty object for anything that is not a plain object.
 */
export function extractUnmodeledFields(raw: unknown): Record<string, unknown> {
  if (!raw || typeof raw !== "object") return {};
  const extras: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(raw as Record<string, unknown>)) {
    if (!MODELED_KEYS.has(key)) extras[key] = value;
  }
  return extras;
}

/** Member lookup ignoring letter case, mirroring the C# converter's own key matching. */
function findMember(owner: Record<string, unknown>, name: string): unknown {
  const key = Object.keys(owner).find((k) => k.toLowerCase() === name.toLowerCase());
  return key === undefined ? undefined : owner[key];
}

function countNames(owner: Record<string, unknown>, legacyName: string): number {
  const list = findMember(owner, legacyName);
  return Array.isArray(list) ? list.filter((x) => typeof x === "string").length : 0;
}

function countGroupNames(raw: Record<string, unknown>, group: string): number {
  const g = findMember(raw, group);
  if (!g || typeof g !== "object") return 0;
  const r = g as Record<string, unknown>;
  return countNames(r, "Whitelist") + countNames(r, "Blacklist");
}

/**
 * True when a routing-map key is the invariant decimal spelling of a non-negative `int` — what a
 * converted map holds. Bounded at int.MaxValue because the C# side parses with `int.TryParse`, where a
 * larger number fails to parse and is read as a tag NAME; an unbounded check here would disagree.
 */
function isIdKey(key: string): boolean {
  const n = Number(key);
  return Number.isInteger(n) && n >= 0 && n <= 2147483647 && String(n) === key;
}

function countNameKeyedDestinations(raw: Record<string, unknown>): number {
  const map = findMember(raw, "TagDestinations");
  if (!map || typeof map !== "object") return 0;
  return Object.keys(map as Record<string, unknown>).filter((k) => !isIdKey(k)).length;
}

/**
 * True when a stored blob still holds tag or performer rules keyed on NAMES, which the backend's
 * one-time conversion has not resolved to ids yet.
 *
 * Deliberately the same predicate the backend scans with (`OptionsMigration.Scan(...).Any`): count the
 * names awaiting an id, never the legacy keys present. The pre-migration panel serialised its whole
 * defaults object, so an install that configured neither group still stores an empty `Whitelist`,
 * `Blacklist` and `ExcludeTags`; treating those as pending would lock this panel out of saving forever
 * on a blob that has nothing left to convert.
 *
 * {@link normalizeOptions} rebuilds both groups and `TagDestinations` from the id-valued keys alone, so
 * a save while this is true would persist those emptied fields over rules nothing else keeps a copy of.
 */
export function hasUnmigratedNameRules(raw: unknown): boolean {
  if (!raw || typeof raw !== "object") return false;
  const r = raw as Record<string, unknown>;
  return (
    countNames(r, "ExcludeTags") +
      countGroupNames(r, "Tags") +
      countGroupNames(r, "Performers") +
      countNameKeyedDestinations(r) >
    0
  );
}

/**
 * Rebuild a fully-canonical {@link RenamerOptions} from an untrusted/legacy blob, reading only the known
 * PascalCase keys and dropping everything else (including stale camelCase duplicates). Returns
 * cloneDefaults() when `raw` is null/not-an-object.
 *
 * The PascalCase reads here are not redundant with the derived types above. {@link RenamerOptions} is
 * derived from a camelCase source, but the SPELLING it derives to is the stored blob's, and this is
 * the only place that fact is enforced against untrusted data — a camelCase read added "to match the
 * generated source" would let a legacy blob's stale duplicate back in and re-create the dual-source
 * preview bug this function exists to fix.
 */
export function normalizeOptions(raw: unknown): RenamerOptions {
  if (!raw || typeof raw !== "object") return cloneDefaults();
  const r = raw as Record<string, unknown>;
  const d = DEFAULT_OPTIONS;
  return {
    FilenameTemplate: str(r.FilenameTemplate, d.FilenameTemplate),
    FolderTemplate: str(r.FolderTemplate, d.FolderTemplate),
    FolderRoot: str(r.FolderRoot, d.FolderRoot),
    DateFormat: str(r.DateFormat, d.DateFormat),
    DurationFormat: str(r.DurationFormat, d.DurationFormat),
    Performers: normalizeMultiValue(r.Performers, d.Performers),
    Tags: normalizeMultiValue(r.Tags, d.Tags),
    IllegalReplacement: str(r.IllegalReplacement, d.IllegalReplacement),
    SpaceReplacement: str(r.SpaceReplacement, d.SpaceReplacement),
    RemoveCharacters: str(r.RemoveCharacters, d.RemoveCharacters),
    Case: caseTransform(r.Case),
    AsciiTransliterate: bool(r.AsciiTransliterate, d.AsciiTransliterate),
    NormalizePunctuation: bool(r.NormalizePunctuation, d.NormalizePunctuation),
    FilenameMax: num(r.FilenameMax, d.FilenameMax),
    FullPathMax: num(r.FullPathMax, d.FullPathMax),
    CrossVolumeConcurrency: num(r.CrossVolumeConcurrency, d.CrossVolumeConcurrency),
    SameVolumeConcurrency: num(r.SameVolumeConcurrency, d.SameVolumeConcurrency),
    DropOrder: strArray(r.DropOrder, [...d.DropOrder]),
    OnlyOrganized: bool(r.OnlyOrganized, d.OnlyOrganized),
    FilenameAsTitle: bool(r.FilenameAsTitle, d.FilenameAsTitle),
    RequiredFields: strArray(r.RequiredFields, [...d.RequiredFields]),
    DuplicateSuffixFormat: str(r.DuplicateSuffixFormat, d.DuplicateSuffixFormat),
    AutoRenamerOnUpdate: bool(r.AutoRenamerOnUpdate, d.AutoRenamerOnUpdate),
    StudioDestinations: destinationMap(r.StudioDestinations),
    TagDestinations: destinationMap(r.TagDestinations),
    PathDestinations: pathDestinations(r.PathDestinations),
    ExcludeTagIds: numArray(r.ExcludeTagIds, []),
    ExcludeStudioIds: numArray(r.ExcludeStudioIds, []),
    ExcludePaths: excludeRules(r.ExcludePaths),
    AllowedRoots: strArray(r.AllowedRoots, []),
    AssociatedExtensions: strArray(r.AssociatedExtensions, [...d.AssociatedExtensions]),
    UnorganizedDestination: destination(r.UnorganizedDestination),
    EnableStudioDestinations: bool(r.EnableStudioDestinations, d.EnableStudioDestinations),
    EnableTagDestinations: bool(r.EnableTagDestinations, d.EnableTagDestinations),
    EnableAdvancedRouting: bool(r.EnableAdvancedRouting, d.EnableAdvancedRouting),
    RemoveEmptyFolder: bool(r.RemoveEmptyFolder, d.RemoveEmptyFolder),
    SqueezeStudioNames: bool(r.SqueezeStudioNames, d.SqueezeStudioNames),
    FieldReplacers: fieldReplacers(r.FieldReplacers),
    StripLeadingArticles: bool(r.StripLeadingArticles, d.StripLeadingArticles),
    Articles: strArray(r.Articles, [...d.Articles]),
    PreventTitlePerformer: bool(r.PreventTitlePerformer, d.PreventTitlePerformer),
    PreventConsecutiveSegments: bool(r.PreventConsecutiveSegments, d.PreventConsecutiveSegments),
  };
}
