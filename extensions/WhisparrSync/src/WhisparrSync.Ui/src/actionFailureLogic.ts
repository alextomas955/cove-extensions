/**
 * Pure action-failure classification: maps a mutating endpoint's raw HTTP status + JSON body string
 * into one of a fixed set of failure kinds, then maps each kind to a friendly, pre-written sentence, so
 * the raw status/body never reaches rendered text. Kept as a zero-import module (like
 * connectionResult.ts) so it compiles in isolation and the node:test logic-guard can import the exact
 * shipped artifact. The version-capability copy is a caller-supplied parameter (not an import)
 * specifically so this module stays zero-import and node:test-checkable in isolation, mirroring
 * connectionResult.ts.
 */

/** The failure classes a mutating endpoint's status + body collapses into. */
export type ActionFailureKind =
  | "badKey"
  | "notWhisparr"
  | "unreachable"
  | "rejected"
  | "noIdentity"
  | "versionUnsupported"
  | "unknown";

/**
 * Classifies a mutating endpoint's raw response into one of the {@link ActionFailureKind}s. A 502
 * carries the `FailureDiscriminator`'s `result` field (`badKey` / `notWhisparr` / `rejected` /
 * anything else, the transport catch-all `unreachable` — matches the C# discriminator); a 400
 * carries a `code` field (`NO_STASHDB_IDENTITY` / `VERSION_UNSUPPORTED` / anything else, `unknown`).
 * Any other status is `unknown`. An unparseable body never throws — it is treated the same as an
 * absent body.
 */
export function classifyActionFailure(status: number, body: string | null): ActionFailureKind {
  let parsed: { result?: unknown; code?: unknown; message?: unknown } | null = null;
  if (body) {
    try {
      parsed = JSON.parse(body) as { result?: unknown; code?: unknown; message?: unknown };
    } catch {
      parsed = null;
    }
  }

  if (status === 502) {
    if (parsed?.result === "badKey") return "badKey";
    if (parsed?.result === "notWhisparr") return "notWhisparr";
    // Whisparr was reached but rejected the request (carries its own message) — distinct from unreachable.
    if (parsed?.result === "rejected") return "rejected";
    return "unreachable";
  }
  if (status === 400) {
    if (parsed?.code === "NO_STASHDB_IDENTITY") return "noIdentity";
    if (parsed?.code === "VERSION_UNSUPPORTED") return "versionUnsupported";
    return "unknown";
  }
  return "unknown";
}

/**
 * Maps a classified {@link ActionFailureKind} to its friendly sentence. `versionUnsupported` returns
 * the caller-supplied `versionCapabilityCopy` verbatim. `rejected` interpolates Whisparr's own
 * `whisparrMessage` (a reached-but-declined request has a real reason worth showing over a generic
 * "can't reach Whisparr"); a null message degrades to a generic sentence.
 */
export function actionFailureMessage(
  kind: ActionFailureKind,
  versionCapabilityCopy: string,
  whisparrMessage?: string | null,
): string {
  switch (kind) {
    case "badKey":
      return "Whisparr rejected the saved API key. Check it in Settings.";
    case "notWhisparr":
      return "That address didn't respond like Whisparr. Check the URL in Settings.";
    case "unreachable":
      return "Can't reach Whisparr right now. Check that it's running.";
    case "rejected":
      return whisparrMessage && whisparrMessage.trim().length > 0
        ? `Whisparr couldn't complete this: ${whisparrMessage.trim()}`
        : "Whisparr couldn't complete this action.";
    case "noIdentity":
      return "This item isn't linked to a metadata id Whisparr can use.";
    case "versionUnsupported":
      return versionCapabilityCopy;
    case "unknown":
      return "Something went wrong. Try again in a moment.";
  }
}

/** Extracts Whisparr's own error message from a `rejected` 502 body, or null if absent/unparseable. */
export function rejectedMessage(body: string | null): string | null {
  if (!body) {
    return null;
  }
  try {
    const parsed = JSON.parse(body) as { message?: unknown };
    return typeof parsed.message === "string" && parsed.message.trim().length > 0
      ? parsed.message
      : null;
  } catch {
    return null;
  }
}

/**
 * The full friendly failure line a caller renders: `Couldn't {label} — {message}`. Never
 * interpolates the raw status or body — only the classified, pre-written message.
 */
export function actionFailureCopy(
  label: string,
  status: number,
  body: string | null,
  versionCapabilityCopy: string,
): string {
  const kind = classifyActionFailure(status, body);
  return `Couldn't ${label} — ${actionFailureMessage(kind, versionCapabilityCopy, rejectedMessage(body))}`;
}
