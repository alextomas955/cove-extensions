/**
 * Pure connection-result logic: the mapping from the backend's `result` discriminator to the
 * distinct copy + tone the panel renders, plus the detected-version → selector mapping. Kept as
 * a zero-import module (like the shared UI module's primitivesLogic/entityPickerLogic) so it compiles in isolation and
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
 * success, and the refused version string on a version mismatch.
 *
 * `url` is the address the probe was sent to. Every class carries it, not just unreachable, because a
 * reading is a fact about ONE address and {@link readingForAddress} compares it. A class that omitted it
 * would never be shown at all, so the field is required and the omission is a typecheck failure.
 */
export interface ConnectionResult {
  kind: ConnectionResultKind;
  url: string;
  instanceName?: string;
  version?: string;
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
 * distinct message (the version-mismatch refusal is amber/warning, not a generic "failed") so the user always knows
 * which of the four things went wrong. Each class's copy is fixed wording.
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
        message: `Couldn't reach Whisparr at ${result.url}. Check the URL and that Whisparr is running.`,
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
 * The reading to show for the address the form currently holds, or null when there is none to show.
 *
 * A probe answers for the address it was sent to, so once the field holds a different host the answer
 * describes an instance the form has stopped pointing at. The answer is returned unchanged or withheld,
 * never reworded — the test it reports genuinely happened, and any other sentence would be a result
 * nothing measured. Withholding is non-destructive: the caller's reading is untouched, so the same
 * sentence returns if the address does.
 *
 * Hosts compare under the trailing-slash-trimmed, case-insensitive rule `WhisparrOptions.WithSubmitted`
 * applies to the persisted version stamp. One rule for both keeps the transient banner and the stored
 * reading in agreement about which edits repoint a connection.
 */
export function readingForAddress(
  reading: ConnectionResult | null,
  address: string,
): ConnectionResult | null {
  if (reading === null) {
    return null;
  }
  const host = (value: string) => value.replace(/\/+$/, "").toLowerCase();
  return host(reading.url) === host(address) ? reading : null;
}

/**
 * The version selector to auto-select from a detected version string: major 3 → "v3", major 2 →
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
