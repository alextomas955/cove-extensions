/**
 * The server error-body decoding the status stores share. A failed request's body is either a
 * `{ code }` outcome or something else (a non-JSON gateway error, an empty body); the stores branch
 * on exactly one code — a Whisparr v2 capability gap — so that decision lives here, extracted
 * import-free so the parse-and-match rule is offline-testable (the `*Logic.ts` gate compiles this
 * file alone). Callers keep the `err instanceof ApiError` narrowing themselves and pass `err.body`.
 */

/** The connected Whisparr GENERATION does not offer this capability. */
export const VERSION_UNSUPPORTED = "VERSION_UNSUPPORTED";

/**
 * The connected BUILD does not declare the routes this operation needs.
 *
 * Deliberately distinct from {@link VERSION_UNSUPPORTED}, because the server emits them separately: a v3
 * instance missing a route is not a version mismatch, and only one of the two is answered by connecting a
 * different generation. Both are permanent for the instance the user is connected to, so neither may be
 * rendered as something a retry could clear.
 */
export const CAPABILITY_UNAVAILABLE = "CAPABILITY_UNAVAILABLE";

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

/** Does this error body signal that the connected build declares none of the needed routes? */
export function isCapabilityUnavailableBody(body: string): boolean {
  return errorCode(body) === CAPABILITY_UNAVAILABLE;
}
