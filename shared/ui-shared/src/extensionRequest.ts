// Not in the barrel: a consumer reaches this through the `./extensionRequest` subpath, so importing
// the barrel never takes the error class with it.
//
// The requests themselves are each extension's generated client, bound to the host's authenticated
// fetch. What stays here is the shape a failure arrives in, which every error branch reads.

/**
 * A request the server answered with a status outside 2xx.
 *
 * Thrown by the interceptor each extension registers over its generated client, so an error branch
 * can read the status the server sent rather than infer one. Two failures are deliberately not one
 * of these: a request that never completed - a dropped connection, an abort - and a 2xx reply the
 * client could not read. Neither carries a status the server chose, and an ApiError would claim it
 * did. That is what lets a `catch` tell "the server said no" from "nobody knows".
 */
export class ApiError extends Error {
  /** The status the server answered with. */
  readonly status: number;

  /** What a reader is shown: a problem body's `detail`, else whatever the route sent. */
  readonly body: string;

  /** The route, as a path. Not the absolute url: this is the spelling the server's own logs use. */
  readonly path: string;

  constructor(status: number, body: string, path: string) {
    super(`API ${String(status)} ${path}: ${body}`);
    this.status = status;
    this.body = body;
    this.path = path;
    this.name = "ApiError";
  }
}

/**
 * Whether the server declined the request before acting on it, so the work it asked for did not
 * begin.
 *
 * A 4xx is the server stating it will not do the work: an extension route authorizes, validates and
 * checks for a pending conversion before it touches anything, so a status in that range is a
 * decision taken in front of the side effect. A 5xx is the server failing while carrying the work
 * out, which says nothing about how far it got. A failure with no status at all - a dropped
 * connection, an abort, a reply that would not parse - says less still.
 *
 * A caller reporting a destructive operation may tell the user nothing changed only when this is
 * true. Every other failure leaves the outcome unknown and has to say so.
 */
export function isRefusal(err: unknown): boolean {
  return err instanceof ApiError && err.status < 500;
}

/** A request failure as a user reads it: an ApiError's status and body, else the error's message. */
export function errorText(err: unknown): string {
  if (err instanceof ApiError) return `${String(err.status)} ${err.body}`;
  return err instanceof Error ? err.message : String(err);
}
