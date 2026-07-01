/**
 * TS mirror of `src/Renamer/Options/RenamerOptions.cs`.
 *
 * Property names are PascalCase to match the C# property spelling exactly; the C# side
 * deserializes with `PropertyNameCaseInsensitive = true`, so casing is forgiving, but
 * mirroring the C# spelling keeps the contract self-documenting. The three enums serialize
 * as STRINGS (C# `JsonStringEnumConverter`) — the value spelling ("None", "DropAll", …) is
 * the wire value, so these are string-union types here.
 *
 * DEFAULT_OPTIONS reproduces the C# record's default initializers verbatim, so a first-run
 * panel (no stored "options" blob) shows the same defaults the backend would apply.
 */

/** Optional case transform applied to a rendered name. C# enum: `CaseTransform`. */
export type CaseTransform = "None" | "Lower" | "Title";

/** What to do when a multi-value field exceeds its max count. C# enum: `OverflowPolicy`. */
export type OverflowPolicy = "DropAll" | "KeepFirst";

/**
 * Sort order for a multi-value field's items. C# enum: `SortOrder`.
 * `IdAsc`/`FavoriteFirst` are performer-only (tags fall back to name ordering for them).
 */
export type SortOrder = "NameAsc" | "None" | "IdAsc" | "FavoriteFirst";

/** Per-field controls for a multi-value token (performers, tags). Mirrors C# `MultiValueOptions`. */
export interface MultiValueOptions {
  /** String inserted between joined items. */
  Separator: string;
  /** Maximum items to emit; 0 = unlimited. */
  MaxCount: number;
  /** Behavior when MaxCount is exceeded. */
  OnOverflow: OverflowPolicy;
  /** Sort applied before joining. */
  Sort: SortOrder;
  /** If non-empty, only these values are kept (case-insensitive). */
  Whitelist: string[];
  /** If non-empty, these values are removed (case-insensitive). */
  Blacklist: string[];
  /** Performer-only: genders dropped before the max-count limit (case-insensitive). */
  IgnoreGenders: string[];
  /** Performer-only: preferred gender ordering, most-preferred first (case-insensitive). */
  GenderOrder: string[];
}

/** One source-path → destination routing rule. Mirrors C# `PathDestinationRule`. */
export interface PathDestinationRule {
  /** Exact source path, or a regex when `IsRegex` is true. */
  Pattern: string;
  /** Absolute destination-root template the matched item routes to. */
  Dest: string;
  /** When true, `Pattern` is interpreted as a regex; otherwise an exact match. */
  IsRegex: boolean;
}

/** One source-path exclude rule (carries no destination). Mirrors C# `ExcludeRule`. */
export interface ExcludeRule {
  /** Exact source path, or a regex when `IsRegex` is true. */
  Pattern: string;
  /** When true, `Pattern` is interpreted as a regex; otherwise an exact match. */
  IsRegex: boolean;
}

/** One per-token literal find/replace rule. Mirrors C# `FieldReplaceRule`. */
export interface FieldReplaceRule {
  /** Canonical token name (case-insensitive) whose value this rule rewrites. */
  TargetToken: string;
  /** Literal substring to find (NOT a regex). An empty find is a no-op. */
  Find: string;
  /** Literal replacement substring. */
  Replace: string;
}

/** All rename settings. Mirrors C# `RenamerOptions`. */
export interface RenamerOptions {
  FilenameTemplate: string;
  FolderTemplate: string;
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
  FilenameMax: number;
  FullPathMax: number;
  DropOrder: string[];
  OnlyOrganized: boolean;
  /** Use the basename (without extension) as $title when an item has none. */
  FilenameAsTitle: boolean;
  RequiredFields: string[];
  DuplicateSuffixFormat: string;
  AutoRenamerOnUpdate: boolean;

  // Routing maps — id/name → destination-root template. StudioDestinations keys on the stable
  // studio id; TagDestinations keys on the tag name (compared case-insensitively by the backend).
  StudioDestinations: Record<number, string>;
  TagDestinations: Record<string, string>;
  // Source-path routing rules, in user order.
  PathDestinations: PathDestinationRule[];
  // Excludes (evaluated first): tag names, stable studio ids, and source-path rules.
  ExcludeTags: string[];
  ExcludeStudioIds: number[];
  ExcludePaths: ExcludeRule[];
  // The roots a rename may write into; default-relocate + unorganized destinations and their gate.
  AllowedRoots: string[];
  // Extra sidecar extensions whose same-basename file moves with the primary (supplementing the
  // DB-tracked captions); a target that already exists is skipped, never overwritten.
  AssociatedExtensions: string[];
  DefaultDestination: string;
  UnorganizedDestination: string;
  EnableDefaultRelocate: boolean;
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
 * The C# defaults (RenamerOptions.cs):
 *   FilenameTemplate "{$date - }$title{ [$height]}", FolderTemplate "", DateFormat "yyyy-MM-dd",
 *   DurationFormat verbatim `hh\-mm\-ss`, Performers.Separator ", ", Tags.Separator " ",
 *   FilenameMax 255, FullPathMax 259, the 9-field DropOrder, RequiredFields ["title"],
 *   DuplicateSuffixFormat " ({n})", Articles ["The","A","An"], FilenameAsTitle true and
 *   PreventConsecutiveSegments true (both on for a fresh install), RemoveEmptyFolder off
 *   (destructive stays opt-in), every routing map {} / list [] and every other flag/string off/empty.
 *
 * The cross-drive safety knobs (FreeSpaceHeadroomBytes / CrossVolumeConcurrency /
 * SameVolumeConcurrency) are intentionally NOT modeled here. The panel never edits them, so leaving
 * them out of DEFAULT_OPTIONS keeps them out of MODELED_KEYS — which is what lets
 * extractUnmodeledFields carry a stored value through a load → save round-trip untouched instead of
 * normalizeOptions consuming (and dropping) it.
 */
export const DEFAULT_OPTIONS: RenamerOptions = {
  FilenameTemplate: "{$date - }$title{ [$height]}",
  FolderTemplate: "",
  DateFormat: "yyyy-MM-dd",
  // C# verbatim string @"hh\-mm\-ss" → the literal value contains single backslashes.
  DurationFormat: "hh\\-mm\\-ss",
  Performers: {
    Separator: ", ",
    MaxCount: 0,
    OnOverflow: "DropAll",
    Sort: "NameAsc",
    Whitelist: [],
    Blacklist: [],
    IgnoreGenders: [],
    GenderOrder: [],
  },
  Tags: {
    Separator: " ",
    MaxCount: 0,
    OnOverflow: "DropAll",
    Sort: "NameAsc",
    Whitelist: [],
    Blacklist: [],
    IgnoreGenders: [],
    GenderOrder: [],
  },
  IllegalReplacement: "",
  SpaceReplacement: "",
  RemoveCharacters: "",
  Case: "None",
  AsciiTransliterate: false,
  FilenameMax: 255,
  FullPathMax: 259,
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
  ExcludeTags: [],
  ExcludeStudioIds: [],
  ExcludePaths: [],
  AllowedRoots: [],
  AssociatedExtensions: [],
  DefaultDestination: "",
  UnorganizedDestination: "",
  EnableDefaultRelocate: false,
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
      Whitelist: [],
      Blacklist: [],
      IgnoreGenders: [],
      GenderOrder: [],
    },
    Tags: {
      ...DEFAULT_OPTIONS.Tags,
      Whitelist: [],
      Blacklist: [],
      IgnoreGenders: [],
      GenderOrder: [],
    },
    DropOrder: [...DEFAULT_OPTIONS.DropOrder],
    RequiredFields: [...DEFAULT_OPTIONS.RequiredFields],
    StudioDestinations: { ...DEFAULT_OPTIONS.StudioDestinations },
    TagDestinations: { ...DEFAULT_OPTIONS.TagDestinations },
    PathDestinations: DEFAULT_OPTIONS.PathDestinations.map((r) => ({ ...r })),
    ExcludeTags: [...DEFAULT_OPTIONS.ExcludeTags],
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
// A routing map can arrive from a hand-edited/legacy blob with non-string values or, for the
// id-keyed map, non-numeric keys. Keep only the entries that conform and rebuild a fresh plain
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
function strKeyStringMap(v: unknown): Record<string, string> {
  const src = asRecord(v);
  const out: Record<string, string> = {};
  for (const [k, val] of Object.entries(src)) {
    if (typeof val === "string") out[k] = val;
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
    Whitelist: strArray(r.Whitelist, []),
    Blacklist: strArray(r.Blacklist, []),
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
    DateFormat: str(r.DateFormat, d.DateFormat),
    DurationFormat: str(r.DurationFormat, d.DurationFormat),
    Performers: normalizeMultiValue(r.Performers, d.Performers),
    Tags: normalizeMultiValue(r.Tags, d.Tags),
    IllegalReplacement: str(r.IllegalReplacement, d.IllegalReplacement),
    SpaceReplacement: str(r.SpaceReplacement, d.SpaceReplacement),
    RemoveCharacters: str(r.RemoveCharacters, d.RemoveCharacters),
    Case: caseTransform(r.Case),
    AsciiTransliterate: bool(r.AsciiTransliterate, d.AsciiTransliterate),
    FilenameMax: num(r.FilenameMax, d.FilenameMax),
    FullPathMax: num(r.FullPathMax, d.FullPathMax),
    DropOrder: strArray(r.DropOrder, [...d.DropOrder]),
    OnlyOrganized: bool(r.OnlyOrganized, d.OnlyOrganized),
    FilenameAsTitle: bool(r.FilenameAsTitle, d.FilenameAsTitle),
    RequiredFields: strArray(r.RequiredFields, [...d.RequiredFields]),
    DuplicateSuffixFormat: str(r.DuplicateSuffixFormat, d.DuplicateSuffixFormat),
    AutoRenamerOnUpdate: bool(r.AutoRenamerOnUpdate, d.AutoRenamerOnUpdate),
    StudioDestinations: numKeyStringMap(r.StudioDestinations),
    TagDestinations: strKeyStringMap(r.TagDestinations),
    PathDestinations: pathDestinations(r.PathDestinations),
    ExcludeTags: strArray(r.ExcludeTags, []),
    ExcludeStudioIds: numArray(r.ExcludeStudioIds, []),
    ExcludePaths: excludeRules(r.ExcludePaths),
    AllowedRoots: strArray(r.AllowedRoots, []),
    AssociatedExtensions: strArray(r.AssociatedExtensions, [...d.AssociatedExtensions]),
    DefaultDestination: str(r.DefaultDestination, d.DefaultDestination),
    UnorganizedDestination: str(r.UnorganizedDestination, d.UnorganizedDestination),
    EnableDefaultRelocate: bool(r.EnableDefaultRelocate, d.EnableDefaultRelocate),
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
