/**
 * Reads the `ErrorCode` body a refused route answers with.
 *
 * The body is read for its declared members alone, never for its text: one generation answers a
 * refusal with a full stack trace in the body.
 */
import type { components } from "../../wire/api";

type ErrorCode = components["schemas"]["ErrorCode"];

/**
 * The code and bound `body` names, or null where it names no code this build can read.
 *
 * A body with no such member is a live path, not a guarded-against one: a route can refuse with
 * an empty body, and a proxy can answer with something that is not JSON at all.
 */
export function errorCodeIn(body: string): ErrorCode | null {
  let named: unknown;
  try {
    named = JSON.parse(body);
  } catch {
    return null;
  }

  if (typeof named !== "object" || named === null || !("code" in named)) return null;
  if (typeof named.code !== "string") return null;

  const bound = "max" in named ? named.max : null;
  return { code: named.code, max: typeof bound === "number" ? bound : null };
}
