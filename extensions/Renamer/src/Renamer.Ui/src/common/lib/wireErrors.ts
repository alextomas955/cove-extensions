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

/** The thrown body as a reader sees it: a string stays itself, anything else is re-serialized. */
function bodyText(error: unknown): string {
  if (typeof error === "string") return error;
  if (error instanceof Error) return error.message;
  try {
    // A value that serializes to nothing falls back to the empty string, which the caller reads as
    // no body and replaces with the status text.
    return JSON.stringify(error) || "";
  } catch {
    return String(error);
  }
}

client.interceptors.error.use((error, response) => {
  // No response means the request never completed - a network failure or an abort - and the thrown
  // value is already the reason. An ApiError there would claim a status the server never sent.
  if (!response) return error;
  return new ApiError(response.status, bodyText(error) || response.statusText, response.url);
});
