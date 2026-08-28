/**
 * The settings panel's view of `src/Renamer/Options/RenamerOptions.cs`.
 *
 * The shapes below are a mechanical re-casing of the generated wire contract rather than a hand
 * transcription of it: `Pascal<>` derives each one from `../wire/api`, so a change to the C# record
 * reaches this panel through the committed document. Per-member documentation stays on those records,
 * which state each rule more fully than a mirrored copy could.
 *
 * PascalCase is the spelling of the PERSISTED options blob on every existing installation, and
 * `MODELED_KEYS` is built from `DEFAULT_OPTIONS`' runtime keys, so re-casing here would make every
 * stored key look unmodeled and leave the two writers disagreeing about the blob's spelling. The enums
 * persist as their member NAMES, so their values are re-cased too even though the wire spells them
 * camelCase.
 *
 * DEFAULT_OPTIONS reproduces the C# record's default initializers verbatim, so a first-run
 * panel (no stored "options" blob) shows the same defaults the backend would apply.
 */

import type * as Wire from "../wire/api";

/**
 * Re-cases a generated wire shape into the persisted PascalCase spelling: object keys and string-enum
 * literals are capitalized, a free-form `string` is left alone, numbers, booleans, `null` and
 * `undefined` pass through unchanged, and an array's element type is mapped. `-?` drops the optional
 * markers the generator emits for a C# property with a default initializer, which the panel always
 * supplies from {@link DEFAULT_OPTIONS}.
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
export type Destination = Pascal<Wire.Destination>;
export type PathDestinationRule = Pascal<Wire.PathDestinationRule>;
export type ExcludeRule = Pascal<Wire.ExcludeRule>;
export type FieldReplaceRule = Pascal<Wire.FieldReplaceRule>;

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
export const NO_DESTINATION: Destination = { Root: CONTAINING_ROOT, Template: "" };

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
 * Case is deliberately not forgiven: a converted root IS the library path's own casing and a picked
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
 * badged until the read has SETTLED successfully: an unsettled read carries an empty list, and
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

  // The picker comes BACK for a stale root even with nothing to pick, because that is the state that
  // stops the rule working: hiding it then would leave the user reading a skip reason with no way to
  // act on it.
  return { chosen, stale, showPicker: library.paths.length > 0 || stale, notice };
}

/** All rename settings. Mirrors C# `RenamerOptions`. */
export interface RenamerOptions {
  FilenameTemplate: string;
  /** The DEFAULT destination's relative folder template, rendered under {@link FolderRoot}. */
  FolderTemplate: string;
  /** The DEFAULT destination's root: a Cove library path, or {@link CONTAINING_ROOT}. */
  FolderRoot: string;
  DateFormat: string;
  DurationFormat: string;
  Performers: MultiValueOptions;
  Tags: MultiValueOptions;
  IllegalReplacement: string;
  SpaceReplacement: string;
  /** Literal characters dropped from the name outright, ahead of illegal/space handling. */
  RemoveCharacters: string;
  Case: CaseTransform;
  AsciiTransliterate: boolean;
  NormalizePunctuation: boolean;
  FilenameMax: number;
  FullPathMax: number;
  /** Simultaneous cross-drive transfers per source→destination disk pair. */
  CrossVolumeConcurrency: number;
  /** Simultaneous same-drive renames in a batch. */
  SameVolumeConcurrency: number;
  DropOrder: string[];
  OnlyOrganized: boolean;
  /** Use the basename (without extension) as $title when an item has none. */
  FilenameAsTitle: boolean;
  RequiredFields: string[];
  DuplicateSuffixFormat: string;
  AutoRenamerOnUpdate: boolean;

  // Routing maps: stable entity id → destination. A rule keys on the id and never on the name, so a
  // rename in Cove cannot orphan it and two case variants of one name cannot route to two
  // destination trees. JSON object keys are strings, so every key a save writes must still parse as
  // an integer: the backend binds these as `Dictionary<int, Destination>` and answers a bind failure
  // with DEFAULTS, discarding every setting in the blob.
  StudioDestinations: Record<number, Destination>;
  TagDestinations: Record<number, Destination>;
  // Source-path routing rules, in user order.
  PathDestinations: PathDestinationRule[];
  // Excludes (evaluated first): stable tag ids, stable studio ids, and source-path rules.
  ExcludeTagIds: number[];
  ExcludeStudioIds: number[];
  ExcludePaths: ExcludeRule[];
  // Extra sidecar extensions whose same-basename file moves with the primary (supplementing the
  // DB-tracked captions); a target that already exists is skipped, never overwritten.
  AssociatedExtensions: string[];
  /** The route for an un-curated item, or `null` when there is no unorganized route. */
  UnorganizedDestination: Destination | null;
  EnableStudioDestinations: boolean;
  EnableTagDestinations: boolean;
  EnableAdvancedRouting: boolean;
  /** Delete the source folder after a move, but only when the move leaves it completely empty. */
  RemoveEmptyFolder: boolean;
  // Field-rewrite shaping applied before the template renders.
  SqueezeStudioNames: boolean;
  FieldReplacers: FieldReplaceRule[];
  StripLeadingArticles: boolean;
  Articles: string[];
  // Folder/title de-duplication.
  PreventTitlePerformer: boolean;
  PreventConsecutiveSegments: boolean;
}

/**
 * These mirror the defaults `RenamerOptions.cs` declares, which is where a value is read from. They
 * are not restated here: a copy of a value is a second declaration free to disagree with the first,
 * and nothing type-checks prose.
 *
 * CrossVolumeConcurrency / SameVolumeConcurrency ARE modeled — the Advanced panel edits them, so
 * they belong in DEFAULT_OPTIONS (and therefore MODELED_KEYS + normalizeOptions). FreeSpaceHeadroomBytes
 * is the ONE remaining knob the panel never edits: leaving it out of DEFAULT_OPTIONS keeps it out of
 * MODELED_KEYS, which is what lets extractUnmodeledFields carry a stored value through a load → save
 * round-trip untouched instead of normalizeOptions consuming (and dropping) it.
 */
export const DEFAULT_OPTIONS: RenamerOptions = {
  FilenameTemplate: "{$date - }$title{ [$resolution]}",
  FolderTemplate: "",
  FolderRoot: CONTAINING_ROOT,
  DateFormat: "yyyy-MM-dd",
  // C# verbatim string @"hh\-mm\-ss" → the literal value contains single backslashes.
  DurationFormat: String.raw`hh\-mm\-ss`,
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
  FilenameAsTitle: true,
  RequiredFields: ["title"],
  DuplicateSuffixFormat: " ({n})",
  AutoRenamerOnUpdate: false,
  StudioDestinations: {},
  TagDestinations: {},
  PathDestinations: [],
  ExcludeTagIds: [],
  ExcludeStudioIds: [],
  ExcludePaths: [],
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
    StudioDestinations: cloneDestinationMap(DEFAULT_OPTIONS.StudioDestinations),
    TagDestinations: cloneDestinationMap(DEFAULT_OPTIONS.TagDestinations),
    PathDestinations: DEFAULT_OPTIONS.PathDestinations.map((r) => ({
      ...r,
      Dest: { ...r.Dest },
    })),
    ExcludeTagIds: [...DEFAULT_OPTIONS.ExcludeTagIds],
    ExcludeStudioIds: [...DEFAULT_OPTIONS.ExcludeStudioIds],
    ExcludePaths: DEFAULT_OPTIONS.ExcludePaths.map((r) => ({ ...r })),
    AssociatedExtensions: [...DEFAULT_OPTIONS.AssociatedExtensions],
    FieldReplacers: DEFAULT_OPTIONS.FieldReplacers.map((r) => ({ ...r })),
    Articles: [...DEFAULT_OPTIONS.Articles],
  };
}

// A legacy stored "options" blob can carry STALE camelCase duplicate keys (e.g. `filenameTemplate`,
// `dateFormat`) alongside the canonical PascalCase keys. The old load path spread-merged the raw blob,
// so those stale keys rode into the /preview-sample request body AFTER the live PascalCase ones; the
// backend binds case-insensitively with default last-write-wins, so the stale value overwrote the live
// edit and the preview never changed. normalizeOptions rebuilds a clean, fully-canonical RenamerOptions
// from cloneDefaults() reading ONLY the known PascalCase keys (coerced by declared type), DROPPING every
// unknown/stale key. Applied at the load boundary, it fixes the preview AND self-heals the stored blob on
// the next Save (since the canonical state is what gets persisted). Frontend-only; no backend change.
//
// The id-keyed fields read through the numeric coercers, so a blob still holding the pre-migration
// NAMES coerces to an empty list or map rather than surviving as unusable strings. A parallel field
// holding the old names would re-create the duplicate state the backend's one-time name-to-id
// conversion exists to remove; the panel keeps the erasure and refuses to SAVE instead, while any name
// is still awaiting conversion (hasUnmigratedNameRules below).

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
 * One stored destination, or {@link NO_DESTINATION} when the blob holds something else.
 *
 * A blob written before destinations became objects holds a bare STRING here. Such a value cannot be
 * placed without Cove's library paths, which is why the backend's one-time conversion owns that
 * decision; this panel refuses to guess and shows the moves-nothing destination instead.
 */
function destination(v: unknown): Destination {
  if (!v || typeof v !== "object") return { ...NO_DESTINATION };
  const r = v as Record<string, unknown>;
  return { Root: str(r.Root, CONTAINING_ROOT), Template: str(r.Template, "") };
}

// A routing map can arrive from a hand-edited/legacy blob with non-conforming values or non-numeric
// keys (every routing map is id-keyed). Keep only the entries whose key conforms and rebuild a fresh
// plain object, so a malformed map yields a safe shape rather than propagating bad data.
function numKeyDestinationMap(v: unknown): Record<number, Destination> {
  const src = asRecord(v);
  const out: Record<number, Destination> = {};
  for (const [k, val] of Object.entries(src)) {
    const n = Number(k);
    if (Number.isInteger(n)) out[n] = destination(val);
  }
  return out;
}

/** A fresh copy of a destination map, so a clone shares no nested object with its source. */
function cloneDestinationMap(map: Record<number, Destination>): Record<number, Destination> {
  const out: Record<number, Destination> = {};
  for (const [k, v] of Object.entries(map)) out[Number(k)] = { ...v };
  return out;
}

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
 * Adapt the editor's string-keyed map back to the persisted `Record<number, string>`.
 *
 * Keeps only the entries {@link numKeyStringMap} would keep, so the two agree entry for entry: a
 * hand-edited or legacy blob can carry a non-integer key ("x", "1.5"), and a key the backend cannot
 * parse as an integer fails the whole options bind, which the store answers with defaults.
 */
export function fromStringKeyed(map: Record<string, Destination>): Record<number, Destination> {
  const out: Record<number, Destination> = {};
  for (const [k, v] of Object.entries(map)) {
    const n = Number(k);
    if (Number.isInteger(n)) out[n] = v;
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
            Dest: destination(r.Dest),
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

/** Member lookup ignoring letter case, mirroring the host serializer's own key matching. */
function findMember(owner: Record<string, unknown>, name: string): unknown {
  const key = Object.keys(owner).find((k) => k.toLowerCase() === name.toLowerCase());
  return key === undefined ? undefined : owner[key];
}

/**
 * How many names one legacy list still holds. A blank entry is not a name: the backend's own scan
 * skips one, so counting it would refuse a save the conversion is never going to act on.
 */
function countNames(owner: Record<string, unknown>, legacyKey: string): number {
  const list = findMember(owner, legacyKey);
  return Array.isArray(list)
    ? list.filter((x) => typeof x === "string" && x.trim().length > 0).length
    : 0;
}

function countGroupNames(raw: Record<string, unknown>, group: string): number {
  const owner = findMember(raw, group);
  if (!owner || typeof owner !== "object") return 0;
  const r = owner as Record<string, unknown>;
  return countNames(r, "Whitelist") + countNames(r, "Blacklist");
}

/**
 * True when a routing-map key is one the backend reads as an id rather than as a tag name.
 *
 * Parity with `int.TryParse(key, NumberStyles.Integer, InvariantCulture)` down to spellings nobody
 * writes by hand: hence the leading sign, the ASCII-only surrounding whitespace and the leading zeroes
 * an `int` parse accepts, and the int32 bound past which it accepts nothing. Both directions of a
 * disagreement are unrecoverable. A key called a name here that the backend calls an id is never
 * rewritten, since the conversion runs only when its own scan finds work and stamps the blob done
 * otherwise, so Save would be refused forever; a key called an id here that the backend calls a name
 * is one a save erases.
 */
function isIdKey(key: string): boolean {
  if (!/^[ \t\n\v\f\r]*[+-]?\d+[ \t\n\v\f\r]*$/.test(key)) return false;
  const n = Number(key);
  return n >= -2147483648 && n <= 2147483647;
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
 * a save while this is true persists those emptied fields over rules nothing else keeps a copy of.
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

/** A plain object, the only shape the backend's destination walk descends into: a JSON array is not one. */
function plainObject(value: unknown): Record<string, unknown> | undefined {
  return value && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined;
}

/**
 * A destination stored the way it was before it became a root plus a template: a bare path string.
 * That is the whole of what the backend's site walk takes, so the empty string is one too.
 */
function isBarePath(value: unknown): boolean {
  return typeof value === "string";
}

function hasBarePathValue(raw: Record<string, unknown>, field: string): boolean {
  const map = plainObject(findMember(raw, field));
  return map !== undefined && Object.values(map).some(isBarePath);
}

function hasBarePathRule(raw: Record<string, unknown>): boolean {
  const rules = findMember(raw, "PathDestinations");
  return (
    Array.isArray(rules) &&
    rules.some((rule) => {
      const r = plainObject(rule);
      return r !== undefined && isBarePath(findMember(r, "Dest"));
    })
  );
}

/**
 * True when a stored blob still holds destinations as the bare absolute paths they were before a
 * destination became a Cove library root plus a relative template.
 *
 * Deliberately the same sites the backend rewrites (`OptionsMigration.ConvertDestinationsToRoots`):
 * a JSON STRING under either routing map, on a path rule's `Dest`, or on `UnorganizedDestination`.
 * The global folder template and root are strings the conversion leaves exactly as stored, so reading
 * either as a site would refuse a save on every install that configured one - permanently, since the
 * conversion stamps the blob done once it finds no site.
 *
 * {@link normalizeOptions} reads a bare path as {@link NO_DESTINATION}, and `UnorganizedDestination`
 * as no route at all, so a save while this is true persists those blanks over folders nothing else
 * keeps a copy of. That conversion DEFERS while Cove has supplied no library paths, because there
 * would be no root to choose, so the old shape can sit in the store across restarts.
 */
export function hasUnmigratedDestinations(raw: unknown): boolean {
  const r = plainObject(raw);
  if (r === undefined) return false;
  return (
    hasBarePathValue(r, "StudioDestinations") ||
    hasBarePathValue(r, "TagDestinations") ||
    hasBarePathRule(r) ||
    isBarePath(findMember(r, "UnorganizedDestination"))
  );
}

/**
 * Rebuild a fully-canonical {@link RenamerOptions} from an untrusted/legacy blob, reading only the known
 * PascalCase keys and dropping everything else (including stale camelCase duplicates). Returns
 * cloneDefaults() when `raw` is null/not-an-object.
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
    StudioDestinations: numKeyDestinationMap(r.StudioDestinations),
    TagDestinations: numKeyDestinationMap(r.TagDestinations),
    PathDestinations: pathDestinations(r.PathDestinations),
    ExcludeTagIds: numArray(r.ExcludeTagIds, []),
    ExcludeStudioIds: numArray(r.ExcludeStudioIds, []),
    ExcludePaths: excludeRules(r.ExcludePaths),
    AssociatedExtensions: strArray(r.AssociatedExtensions, [...d.AssociatedExtensions]),
    // Absent (or anything that is not an object) is how "there is no unorganized route" is spelled,
    // and it is a different destination from one naming neither a root nor a folder.
    UnorganizedDestination:
      r.UnorganizedDestination && typeof r.UnorganizedDestination === "object"
        ? destination(r.UnorganizedDestination)
        : null,
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
