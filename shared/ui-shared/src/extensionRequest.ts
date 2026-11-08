// Not in the barrel: a consumer reaches this through the `./extensionRequest` subpath, so importing
// the barrel never takes the error class with it.
//
// The requests themselves are each extension's generated client, bound to the host's authenticated
// fetch. What stays here is the shape a failure arrives in, which every error branch reads.

/**
 * A request that reached the server and was refused.
 *
 * Thrown by the interceptor each extension registers over its generated client, so an error branch
 * can read the status the server sent rather than infer one. A failure that never reached the
 * server - a dropped connection, an abort - is not one of these, which is what lets a `catch` tell
 * "the server said no" from "nobody knows".
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

/** A request failure as a user reads it: an ApiError's status and body, else the error's message. */
export function errorText(err: unknown): string {
  if (err instanceof ApiError) return `${String(err.status)} ${err.body}`;
  return err instanceof Error ? err.message : String(err);
}
