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
 * `Dictionary<int, string>` generates as one and JSON object keys are strings either way. Their
 * keys are still stable entity ids and never names — a rename in Cove must not orphan a rule or
 * split one entity across two destination trees — and the coercions on both sides of this module
 * (`numKeyStringMap` here, `fromStringKeyed` in the editors) are what keep them integral.
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
 *   DurationFormat verbatim `hh\-mm\-ss`, Performers.Separator ", ", Tags.Separator " ",
 *   FilenameMax 255, FullPathMax 259, the 9-field DropOrder, RequiredFields ["title"],
 *   DuplicateSuffixFormat " ({n})", Articles ["The","A","An"], FilenameAsTitle true and
 *   PreventConsecutiveSegments true (both on for a fresh install), RemoveEmptyFolder off
 *   (destructive stays opt-in), every routing map {} / list [] and every other flag/string off/empty.
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
  AllowedRoots: [],
  AssociatedExtensions: [],
  UnorganizedDestination: "",
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
    PathDestinations: DEFAULT_OPTIONS.PathDestinations.map((r) => ({ ...r })),
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
// A routing map can arrive from a hand-edited/legacy blob with non-string values or non-numeric keys
// (every routing map is id-keyed). Keep only the entries that conform and rebuild a fresh plain
// object, so a malformed map yields a safe shape rather than propagating bad data.
function numKeyStringMap(v: unknown): Record<number, string> {
  const src = asRecord(v);
  const out: Record<number, string> = {};
  for (const [k, val] of Object.entries(src)) {
    const n = Number(k);
    if (Number.isInteger(n) && typeof val === "string") out[n] = val;
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
            Dest: str(r.Dest, ""),
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
    StudioDestinations: numKeyStringMap(r.StudioDestinations),
    TagDestinations: numKeyStringMap(r.TagDestinations),
    PathDestinations: pathDestinations(r.PathDestinations),
    ExcludeTagIds: numArray(r.ExcludeTagIds, []),
    ExcludeStudioIds: numArray(r.ExcludeStudioIds, []),
    ExcludePaths: excludeRules(r.ExcludePaths),
    AllowedRoots: strArray(r.AllowedRoots, []),
    AssociatedExtensions: strArray(r.AssociatedExtensions, [...d.AssociatedExtensions]),
    UnorganizedDestination: str(r.UnorganizedDestination, d.UnorganizedDestination),
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
