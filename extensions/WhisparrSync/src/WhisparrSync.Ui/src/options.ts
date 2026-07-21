/**
 * TS mirror of the server-side WhisparrSync options. Models the connection basics, the selected Whisparr
 * version, and the persisted quality-profile selection. Property names are PascalCase to
 * match the C# spelling.
 *
 * The API key is NEVER modeled on the client. The server returns only a `hasApiKey` boolean; the
 * value stays server-side and is never echoed back into the UI. {@link optionsFromServer} enforces this by
 * reading ONLY the known-safe fields from an untrusted server response — a key, if one were ever wrongly
 * included, is dropped on the floor rather than bound.
 */
import type { WhisparrVersion } from "./connectionResult";

/** One saved per-version connection as the UI sees it — the raw API key is never included. */
export interface ConnectionView {
  BaseUrl: string;
  QualityProfileId: number;
  hasApiKey: boolean;
}

export interface WhisparrOptions {
  BaseUrl: string;
  /** The selected Whisparr API generation; auto-set to the detected version after a successful test. */
  SelectedVersion: WhisparrVersion;
  /** The stable id of the chosen quality profile (0 = none picked yet), sourced from the auto-populated list. */
  QualityProfileId: number;
  /** True when a key is stored server-side; the value itself is never returned to the UI. */
  hasApiKey: boolean;
  /**
   * The last-saved connection per Whisparr version (keyed "v3"/"v2"), so toggling the version selector restores
   * that version's URL / root / profile (and a key-is-set indicator) instead of blanking them. The active
   * version is always present here even before its first per-version save.
   */
  SavedConnections: Record<string, ConnectionView>;
  /** Add-default: the tags applied to items Whisparr adds (defaults to `cove` — how the extension marks its adds). */
  TagsOnAdd: string[];
  /** Add-default: whether newly-added items are monitored by default. */
  MonitorNewByDefault: boolean;
  /** Add-default: whether a monitored item may grab a quality upgrade above its current release. */
  AllowQualityUpgrades: boolean;
}

/** The design's default tag on add — the marker the extension uses to recognise what it added (reconciliation). */
export const DEFAULT_TAG_ON_ADD = "cove";

export const DEFAULT_OPTIONS: WhisparrOptions = {
  BaseUrl: "",
  SelectedVersion: "v3",
  QualityProfileId: 0,
  hasApiKey: false,
  TagsOnAdd: [DEFAULT_TAG_ON_ADD],
  MonitorNewByDefault: true,
  AllowQualityUpgrades: true,
  SavedConnections: {},
};

/** Clone of the defaults so callers can mutate form state without touching the const (the tag list is copied too). */
export function cloneDefaults(): WhisparrOptions {
  return { ...DEFAULT_OPTIONS, TagsOnAdd: [...DEFAULT_OPTIONS.TagsOnAdd], SavedConnections: {} };
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
function version(v: unknown): WhisparrVersion {
  return v === "v2" ? "v2" : "v3";
}

/**
 * Read the per-version saved connections from an untrusted response, keeping ONLY the known-safe fields (never
 * a key) and ONLY the "v2"/"v3" keys. A malformed entry is skipped rather than bound.
 */
function connections(v: unknown): Record<string, ConnectionView> {
  const out: Record<string, ConnectionView> = {};
  if (v && typeof v === "object") {
    for (const [key, val] of Object.entries(v as Record<string, unknown>)) {
      if ((key === "v2" || key === "v3") && val && typeof val === "object") {
        const c = val as Record<string, unknown>;
        out[key] = {
          BaseUrl: str(c.BaseUrl, ""),
          QualityProfileId: num(c.QualityProfileId, 0),
          hasApiKey: bool(c.hasApiKey, false),
        };
      }
    }
  }
  return out;
}

/** Read a string[] of the known-safe tag values; an absent OR empty list falls back to the default `cove` tag. */
function tags(v: unknown): string[] {
  if (Array.isArray(v)) {
    const clean = v.filter((t): t is string => typeof t === "string" && t.length > 0);
    if (clean.length > 0) return clean;
  }
  // The server default is an empty list; the design's default is `cove`, so an empty/absent list seeds it.
  return [DEFAULT_TAG_ON_ADD];
}

/**
 * Rebuild {@link WhisparrOptions} form state from an untrusted `GET /options` response, reading ONLY the
 * known-safe fields (never an API key). Returns {@link cloneDefaults} for a null/non-object input.
 */
export function optionsFromServer(raw: unknown): WhisparrOptions {
  if (!raw || typeof raw !== "object") return cloneDefaults();
  const r = raw as Record<string, unknown>;
  const d = DEFAULT_OPTIONS;
  return {
    BaseUrl: str(r.BaseUrl, d.BaseUrl),
    SelectedVersion: version(r.SelectedVersion),
    QualityProfileId: num(r.QualityProfileId, d.QualityProfileId),
    hasApiKey: bool(r.hasApiKey, d.hasApiKey),
    TagsOnAdd: tags(r.TagsOnAdd),
    MonitorNewByDefault: bool(r.MonitorNewByDefault, d.MonitorNewByDefault),
    AllowQualityUpgrades: bool(r.AllowQualityUpgrades, d.AllowQualityUpgrades),
    SavedConnections: connections(r.SavedConnections),
  };
}
