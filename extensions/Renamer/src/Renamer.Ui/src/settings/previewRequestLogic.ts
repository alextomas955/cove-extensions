/**
 * The pure decision the live-preview hook takes on a request that has settled.
 *
 * Import-free (no React, no request helper, no DOM) so it stays L0 — deterministic and testable with
 * no environment. Extracted for one reason: a debounce timer bounds when a request is *issued*, and
 * nothing about it bounds when a response *arrives*. Once the debounce has elapsed the POST is in
 * flight, and a second one issued after it can answer first, so two responses reach the pane in
 * completion order rather than issue order. Whether a settled response may still be shown is
 * therefore a comparison, not something a cleanup function can express.
 */

/** What the caller does with the settled request. */
export type PreviewAction = "commit" | "discard" | "report-failure";

/**
 * A settled preview request, tagged with the generation it was issued under.
 *
 * `aborted` distinguishes a cancellation the hook itself caused from a request that genuinely failed.
 * It rides on the rejected arm only, because a resolved response was never aborted.
 */
export type SettledPreview =
  | { generation: number; outcome: "resolved" }
  | { generation: number; outcome: "rejected"; aborted: boolean };

/**
 * Decide what to do with `settled`, given the generation now in force.
 *
 * Two rules, both about not lying to the user about their current options:
 *
 * A response is shown only while the generation it was issued under is still the current one. Anything
 * else is a result for options the user has already moved past, and rendering it puts a name in the
 * pane that the settings no longer produce — for a destructive operation, the wrong preview of it.
 *
 * A failure is surfaced only when it belongs to the request the user is waiting on. A superseded
 * request's failure is not the user's error, and neither is an abort at any generation: the hook
 * aborts what it supersedes, so treating that as `previewError` would report a failure it caused
 * itself while a healthy request is still on its way.
 */
export function decideSettledPreview(
  settled: SettledPreview,
  currentGeneration: number,
): PreviewAction {
  if (settled.generation !== currentGeneration) return "discard";
  if (settled.outcome === "resolved") return "commit";
  return settled.aborted ? "discard" : "report-failure";
}
