/**
 * What the live-preview hook does with a request that has settled. Responses arrive in completion
 * order, not issue order, so whether one may still be shown is a comparison of generations.
 */

/** What the caller does with the settled request. */
type PreviewAction = "commit" | "discard" | "report-failure";

/** A settled request and its generation. `aborted` marks a cancellation the hook itself caused. */
type SettledPreview =
  | { generation: number; outcome: "resolved" }
  | { generation: number; outcome: "rejected"; aborted: boolean };

/**
 * Commit a response only while its generation is current, and report a failure only for the current
 * request. A superseded request or an abort is discarded.
 */
export function decideSettledPreview(
  settled: SettledPreview,
  currentGeneration: number,
): PreviewAction {
  if (settled.generation !== currentGeneration) return "discard";
  if (settled.outcome === "resolved") return "commit";
  return settled.aborted ? "discard" : "report-failure";
}
