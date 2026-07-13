/**
 * TS mirror of the server-side WhisparrSync options. This phase (the walking skeleton) models only the
 * connection basics — the full option set (version, root folder, quality profile, webhook secret) is
 * expanded in plan 01-03. Property names are PascalCase to match the C# spelling.
 *
 * CONN-06: the API key is NEVER modeled on the client. The server returns only a `hasApiKey` boolean;
 * the value stays server-side and is never echoed back into the UI.
 */
export interface WhisparrOptions {
  BaseUrl: string;
  /** True when a key is stored server-side; the value itself is never returned to the UI (CONN-06). */
  hasApiKey: boolean;
}

export const DEFAULT_OPTIONS: WhisparrOptions = {
  BaseUrl: "",
  hasApiKey: false,
};

/** Shallow clone of the defaults so callers can mutate form state without touching the const. */
export function cloneDefaults(): WhisparrOptions {
  return { ...DEFAULT_OPTIONS };
}
