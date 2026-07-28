/**
 * Inline, advisory, NON-BLOCKING template/token validators shared by the settings sections.
 *
 * Both render 0..N amber lines and NEVER block Save, remove a value, or feed the persisted shape —
 * purely UX guidance derived from the static token set.
 *
 * SECURITY: every string is a React text node (auto-escaped); the "Did you mean" suggestion is
 * derived from the static TOKENS set, never echoing user markup.
 */
import { AlertTriangle } from "lucide-react";

import { bracesBalanced, unknownTokens, suggestFor, isKnownToken } from "./templateValidation";

/**
 * Renders one amber line for unbalanced braces, one per unknown $token (with a best-effort
 * "Did you mean"), and — for the filename field — one per sample whose /preview-sample flags
 * include "empty" (passed in via emptySamples; reuses the existing debounced preview, no new
 * request). Renders nothing when there are no issues. NEVER feeds Save and never moves the caret.
 */
export function TemplateValidation({
  value,
  emptySamples = [],
}: {
  value: string;
  emptySamples?: string[];
}) {
  const lines: string[] = [];
  if (!bracesBalanced(value)) {
    lines.push("Unmatched { or } — it'll still render, but check your groups.");
  }
  for (const tok of unknownTokens(value)) {
    const suggestion = suggestFor(tok);
    lines.push(
      suggestion
        ? `${tok} isn't a known token — it'll render as empty. Did you mean ${suggestion}?`
        : `${tok} isn't a known token — it'll render as empty.`,
    );
  }
  for (const label of emptySamples) {
    lines.push(`This template produces an empty name for the "${label}" sample.`);
  }
  if (lines.length === 0) return null;
  return (
    <div className="mt-1 space-y-1" role="status" aria-live="polite">
      {lines.map((line) => (
        <p key={line} className="flex items-start gap-1 text-xs text-amber-400">
          <AlertTriangle className="h-3 w-3 shrink-0" />
          <span>{line}</span>
        </p>
      ))}
    </div>
  );
}

/**
 * Invalid-token flagging for the bare-token fields (RequiredFields / DropOrder). Renders one amber
 * line per chip value that is NOT a known token (with a best-effort "Did you mean" from the shared
 * `suggestFor`, displayed as a bare name to match these fields' format). Renders nothing when every
 * value is a known token.
 */
export function TokenAdvisory({ values }: { values: string[] }) {
  const lines: string[] = [];
  for (const value of values) {
    if (isKnownToken(value)) continue;
    const suggestion = suggestFor(value); // returns a `$`-prefixed name or undefined
    const bare = suggestion ? suggestion.slice(1) : undefined;
    lines.push(
      bare
        ? `"${value}" isn't a known token — it'll be ignored. Did you mean ${bare}?`
        : `"${value}" isn't a known token — it'll be ignored.`,
    );
  }
  if (lines.length === 0) return null;
  return (
    <div className="mt-1 space-y-1" role="status" aria-live="polite">
      {lines.map((line) => (
        <p key={line} className="flex items-start gap-1 text-xs text-amber-400">
          <AlertTriangle className="h-3 w-3 shrink-0" />
          <span>{line}</span>
        </p>
      ))}
    </div>
  );
}
