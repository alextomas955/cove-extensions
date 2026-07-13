/**
 * Pure connection-result logic: the mapping from the backend's `result` discriminator (CONN-02) to the
 * distinct copy + tone the panel renders, plus the detected-version → selector mapping (CONN-04). Kept as
 * a zero-import module (like Renamer's primitivesLogic/entityPickerLogic) so it compiles in isolation and
 * the node:test logic-guard can import the exact shipped artifact and assert every failure class gets its
 * own message. No JSX here — the panel picks the icon from {@link ConnectionCopy.tone}.
 */

/** The Whisparr API generation the user has selected / the app detected. */
export type WhisparrVersion = "v3" | "v2";

/** The four failure classes plus success — the backend's `result` discriminator, one-to-one. */
export type ConnectionResultKind =
  "success" | "badKey" | "unreachable" | "notWhisparr" | "versionMismatch";

/**
 * A classified connection outcome with the values its copy interpolates: the instance name + version on
 * success, the attempted URL on unreachable, and the refused version string on a version mismatch.
 */
export interface ConnectionResult {
  kind: ConnectionResultKind;
  instanceName?: string;
  version?: string;
  url?: string;
  detected?: string;
}

/** The tone drives both the {@link StatusText} color and which lucide icon the panel renders. */
export type ConnectionTone = "success" | "error" | "warning";

export interface ConnectionCopy {
  tone: ConnectionTone;
  message: string;
}

/**
 * Maps a classified {@link ConnectionResult} to its user-facing copy + tone. Every class returns a
 * distinct message (VER-04's refusal is amber/warning, not a generic "failed") so the user always knows
 * which of the four things went wrong. Copy is transcribed from the UI-SPEC Copywriting contract.
 */
export function connectionCopy(result: ConnectionResult): ConnectionCopy {
  switch (result.kind) {
    case "success":
      return {
        tone: "success",
        message: `Connected to ${result.instanceName ?? "Whisparr"} — Whisparr ${result.version ?? "unknown"}.`,
      };
    case "badKey":
      return {
        tone: "error",
        message:
          "Whisparr rejected the API key. Check the key in Whisparr → Settings → General and paste it again.",
      };
    case "unreachable":
      return {
        tone: "error",
        message: `Couldn't reach Whisparr at ${result.url ?? "that address"}. Check the URL and that Whisparr is running.`,
      };
    case "notWhisparr":
      return {
        tone: "warning",
        message:
          "Got a web page instead of the Whisparr API. Check the URL points at Whisparr, not a proxy landing page.",
      };
    case "versionMismatch":
      return {
        tone: "warning",
        message: `This looks like Whisparr ${result.detected ?? "of an unknown version"}, which this version can't manage yet. Select a matching version or connect a v3 (Eros) instance.`,
      };
  }
}

/**
 * The version selector to auto-select from a detected version string (CONN-04): major 3 → "v3", major 2 →
 * "v2", anything unparseable → null (leave the current selection untouched). Mirrors the backend's
 * fail-closed major parse so the UI never guesses a selector for a version it can't read.
 */
export function selectorForDetected(version: string | null | undefined): WhisparrVersion | null {
  if (!version) {
    return null;
  }
  const major = Number.parseInt(version.split(".")[0], 10);
  if (major === 3) {
    return "v3";
  }
  if (major === 2) {
    return "v2";
  }
  return null;
}
