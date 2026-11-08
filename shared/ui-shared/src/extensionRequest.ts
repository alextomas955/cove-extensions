// Not in the barrel: this module reaches the SDK, and keeping it behind the `./extensionRequest`
// subpath is what lets a consumer import the barrel without taking it.
//
// The requests themselves are the generated client's, which each extension binds to the host fetch.
// What stays here is the shape a failure arrives in, which every error branch reads.
import { ApiError } from "@cove/extension-sdk";

// Re-exported so every `instanceof ApiError` branch keeps testing the SDK's own class.
export { ApiError };

/** A request failure as a user reads it: an ApiError's status and body, else the error's message. */
export function errorText(err: unknown): string {
  if (err instanceof ApiError) return `${String(err.status)} ${err.body}`;
  return err instanceof Error ? err.message : String(err);
}
