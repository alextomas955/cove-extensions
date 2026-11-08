/**
 * The inline template and token advisories the settings sections share. They never block Save,
 * remove a value, or reach the persisted shape.
 */
import { AlertTriangle } from "lucide-react";

import { bracesBalanced, unknownTokens, suggestFor, isKnownToken } from "./templateLogic";

export function TemplateAdvisories({
  value,
  emptySamples = [],
}: Readonly<{
  value: string;
  emptySamples?: string[];
}>) {
  const lines: string[] = [];
  if (!bracesBalanced(value)) {
    lines.push("Unmatched { or }. It'll still render, but check your groups.");
  }
  for (const tok of unknownTokens(value)) {
    const suggestion = suggestFor(tok);
    lines.push(
      suggestion
        ? `${tok} isn't a known token. It'll render as empty. Did you mean ${suggestion}?`
        : `${tok} isn't a known token. It'll render as empty.`,
    );
  }
  for (const label of emptySamples) {
    lines.push(`This template produces an empty name for the "${label}" sample.`);
  }
  return <AdvisoryLines lines={lines} />;
}

/** Invalid-token flagging for the fields that store a bare token name, without its `$`. */
export function TokenAdvisory({ values }: Readonly<{ values: string[] }>) {
  const lines: string[] = [];
  for (const value of values) {
    if (isKnownToken(value)) continue;
    const suggestion = suggestFor(value); // returns a `$`-prefixed name or undefined
    const bare = suggestion ? suggestion.slice(1) : undefined;
    lines.push(
      bare
        ? `"${value}" isn't a known token. It'll be ignored. Did you mean ${bare}?`
        : `"${value}" isn't a known token. It'll be ignored.`,
    );
  }
  return <AdvisoryLines lines={lines} />;
}

function AdvisoryLines({ lines }: Readonly<{ lines: string[] }>) {
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
