/**
 * What the live-preview hook does with a request that has settled. Responses arrive in completion
 * order, not issue order, so whether one may still be shown is a comparison of generations.
 */

type PreviewAction = "commit" | "discard" | "report-failure";

/** A settled request and its generation. `aborted` marks a cancellation the hook itself caused. */
type SettledPreview =
  | { generation: number; outcome: "resolved" }
  | { generation: number; outcome: "rejected"; aborted: boolean };

export function decideSettledPreview(
  settled: SettledPreview,
  currentGeneration: number,
): PreviewAction {
  if (settled.generation !== currentGeneration) return "discard";
  if (settled.outcome === "resolved") return "commit";
  return settled.aborted ? "discard" : "report-failure";
}
