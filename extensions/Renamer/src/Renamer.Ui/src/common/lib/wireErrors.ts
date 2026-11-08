/**
 * Turns a failed request from the generated client into this extension's {@link ApiError}.
 *
 * The client throws the parsed response body and nothing else, so the status is lost exactly where
 * the code needs it: a superseded scan is a 404 and an unreadable settings blob is a 409, and both
 * read as an ordinary failure without it. A body that parsed to nothing is thrown as a bare object,
 * which is not an Error at all and reaches a `catch` as "[object Object]".
 *
 * The interceptor is handed the Response the throw came from, which is where the status still is.
 *
 * Registered by importing this module for its effect, from the bundle entry, so it is in place
 * before any surface renders.
 */
import { ApiError } from "@cove-extensions/ui-shared/extensionRequest";

import { client } from "../../wire/client.gen";

/** The RFC 9457 members written for a reader, neither of which a problem body must carry. */
interface ProblemText {
  detail?: unknown;
  title?: unknown;
}

/**
 * What a failure says to the person who caused it.
 *
 * A problem body states its own occurrence in `detail` and its kind in `title`, both written to be
 * read. Serializing the envelope instead puts the type URI, the status and the machine token on
 * screen around the one sentence meant for them. A route answering something other than a problem
 * falls back to whatever it did send.
 */
function readable(error: unknown): string {
  if (typeof error === "string") return error;
  if (error instanceof Error) return error.message;
  if (typeof error === "object") {
    const { detail, title } = error as ProblemText;
    if (typeof detail === "string" && detail) return detail;
    if (typeof title === "string" && title) return title;
  }
  try {
    return JSON.stringify(error) || "";
  } catch {
    return String(error);
  }
}

/**
 * The route the failure came from, in the spelling {@link ApiError} documents.
 *
 * A Response carries an absolute url, or an empty one when it was constructed rather than fetched.
 * Parsing is therefore attempted rather than assumed: a throw here would replace the server's own
 * reason with a URL complaint, which is the one thing this interceptor exists to prevent.
 */
function pathOf(response: Response): string {
  try {
    return new URL(response.url).pathname;
  } catch {
    return response.url;
  }
}

client.interceptors.error.use((error, response) => {
  // No response means the request never completed - a network failure or an abort - and the thrown
  // value is already the reason. An ApiError there would claim a status the server never sent.
  if (!response) return error;
  return new ApiError(response.status, readable(error) || response.statusText, pathOf(response));
});
