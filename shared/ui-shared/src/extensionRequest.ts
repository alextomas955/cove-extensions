// Not in the barrel: a consumer reaches this through the `./extensionRequest` subpath, so importing
// the barrel never takes the error class with it.
//
// The requests themselves are each extension's own generated client, bound to the host's
// authenticated fetch in `wireClient.ts`. What lives here is the shape a failure arrives in, the
// interceptor that produces it, and the response shape every panel reads.

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

/**
 * The members a refusal may carry for a reader.
 *
 * `detail` and `title` are RFC 9457 and neither is mandatory. `message` is the Cove host's own
 * spelling: its authorization middleware refuses a request before the extension is reached and
 * writes a plain JSON body, not a problem document.
 */
interface ProblemText {
  detail?: unknown;
  title?: unknown;
  message?: unknown;
}

/**
 * What a failure says to the person who caused it.
 *
 * A problem body states its own occurrence in `detail` and its kind in `title`, both written to be
 * read. Serializing the envelope instead puts the type URI, the status and the machine token on
 * screen around the one sentence meant for them. A refusal the Cove host wrote before the extension
 * was reached carries `message` instead. Anything else falls back to whatever it did send.
 */
function readable(error: unknown): string {
  if (typeof error === "string") return error;
  if (error instanceof Error) return error.message;
  if (typeof error === "object") {
    const { detail, title, message } = error as ProblemText;
    if (typeof detail === "string" && detail) return detail;
    if (typeof title === "string" && title) return title;
    if (typeof message === "string" && message) return message;
  }
  try {
    return JSON.stringify(error) || "";
  } catch {
    return String(error);
  }
}

/**
 * The route a failure came from, in the spelling {@link ApiError} documents.
 *
 * A Response carries an absolute url, or an empty one when it was constructed rather than fetched.
 * Parsing is therefore attempted rather than assumed: a throw here would replace the server's own
 * reason with a URL complaint, which is the one thing the interceptor exists to prevent.
 */
function pathOf(response: Response): string {
  try {
    return new URL(response.url).pathname;
  } catch {
    return response.url;
  }
}

/**
 * Turns a refused request from a generated client into an {@link ApiError}.
 *
 * The client throws the parsed response body and nothing else, so the status is lost exactly where
 * the code needs it: a superseded scan is a 404 and an unreadable settings blob is a 409, and both
 * read as an ordinary failure without it. A body that parsed to nothing is thrown as a bare object,
 * which is not an Error at all and reaches a `catch` as "[object Object]".
 *
 * The interceptor is handed the Response the throw came from, which is where the status still is.
 *
 * The client runs the whole request in one `try`, so a failure reading a reply the server already
 * sent arrives here the same way a refusal does, with the ok Response beside it. The two are told
 * apart below, because nothing downstream could tell them apart afterwards.
 *
 * Each extension registers this on its own client, from a module the bundle entry imports for its
 * effect, so it is in place before any surface renders.
 */
export function apiErrorFromResponse(error: unknown, response: Response | undefined): unknown {
  // No response means the request never completed - a network failure or an abort - and the thrown
  // value is already the reason. An ApiError there would claim a status the server never sent.
  if (!response) return error;

  // The server answered and the client then failed reading the reply: the body did not parse, or it
  // stopped arriving part way. The route ran, so its side effect has happened, and only the account
  // of it was lost. An ApiError here would hand that 200 to the branches that read a status as the
  // server's verdict on work it declined to do, which would report a completed rename or undo as
  // one that changed nothing.
  //
  // The route is not named here. Every branch that renders one of these already says which
  // operation it was waiting on, and a Response the client built rather than fetched carries an
  // empty url, which would leave a gap in the sentence where the route should be.
  if (response.ok) {
    return new Error(`the server's reply could not be read: ${readable(error)}`);
  }

  return new ApiError(response.status, readable(error) || response.statusText, pathOf(response));
}
