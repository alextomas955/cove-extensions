/**
 * TS mirror of the server-side WhisparrSync options. Models the connection basics, the selected Whisparr
 * version, and the persisted root-folder / quality-profile selections. Property names are PascalCase to
 * match the C# spelling.
 *
 * CONN-06: the API key is NEVER modeled on the client. The server returns only a `hasApiKey` boolean; the
 * value stays server-side and is never echoed back into the UI. {@link optionsFromServer} enforces this by
 * reading ONLY the known-safe fields from an untrusted server response — a key, if one were ever wrongly
 * included, is dropped on the floor rather than bound.
 */
import type { WhisparrVersion } from "./connectionResult";

export interface WhisparrOptions {
  BaseUrl: string;
  /** The selected Whisparr API generation; auto-set to the detected version after a successful test (CONN-04). */
  SelectedVersion: WhisparrVersion;
  /** The stable id of the chosen root folder (0 = none picked yet), sourced from the auto-populated list (CONN-05). */
  RootFolderId: number;
  /** The stable id of the chosen quality profile (0 = none picked yet), sourced from the auto-populated list (CONN-05). */
  QualityProfileId: number;
  /** True when a key is stored server-side; the value itself is never returned to the UI (CONN-06). */
  hasApiKey: boolean;
}

export const DEFAULT_OPTIONS: WhisparrOptions = {
  BaseUrl: "",
  SelectedVersion: "v3",
  RootFolderId: 0,
  QualityProfileId: 0,
  hasApiKey: false,
};

/** Shallow clone of the defaults so callers can mutate form state without touching the const. */
export function cloneDefaults(): WhisparrOptions {
  return { ...DEFAULT_OPTIONS };
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
 * Rebuild {@link WhisparrOptions} form state from an untrusted `GET /options` response, reading ONLY the
 * known-safe fields (never an API key — CONN-06). Returns {@link cloneDefaults} for a null/non-object input.
 */
export function optionsFromServer(raw: unknown): WhisparrOptions {
  if (!raw || typeof raw !== "object") return cloneDefaults();
  const r = raw as Record<string, unknown>;
  const d = DEFAULT_OPTIONS;
  return {
    BaseUrl: str(r.BaseUrl, d.BaseUrl),
    SelectedVersion: version(r.SelectedVersion),
    RootFolderId: num(r.RootFolderId, d.RootFolderId),
    QualityProfileId: num(r.QualityProfileId, d.QualityProfileId),
    hasApiKey: bool(r.hasApiKey, d.hasApiKey),
  };
}
