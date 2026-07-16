/**
 * The server error-body decoding the status stores share. A failed request's body is either a
 * `{ code }` outcome or something else (a non-JSON gateway error, an empty body); the stores branch
 * on exactly one code — a Whisparr v2 capability gap — so that decision lives here, extracted
 * import-free so the parse-and-match rule is offline-testable (the `*Logic.ts` gate compiles this
 * file alone). Callers keep the `err instanceof ApiError` narrowing themselves and pass `err.body`.
 */

/** The one code the UI branches on: the connected Whisparr version does not offer this capability. */
export const VERSION_UNSUPPORTED = "VERSION_UNSUPPORTED";

/** The `code` from a `{ code }` error body, or null when the body is absent, non-JSON, or codeless. */
export function errorCode(body: string): string | null {
  try {
    const parsed = JSON.parse(body) as { code?: unknown };
    return typeof parsed.code === "string" ? parsed.code : null;
  } catch {
    return null;
  }
}

/** Does this error body signal a Whisparr v2 version mismatch (`VERSION_UNSUPPORTED`)? */
export function isVersionUnsupportedBody(body: string): boolean {
  return errorCode(body) === VERSION_UNSUPPORTED;
}
