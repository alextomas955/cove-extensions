/**
 * Always-visible token reference. Clicking a chip inserts `$token` at the caret of the
 * last-focused template input (filename or folder — the panel passes the active ref).
 *
 * The token set + order is the canonical `Tokens` constant order from
 * `src/Renamer/Engine/TemplateEngine.cs` (there is NO Tokens.cs — TemplateEngine.cs owns the
 * `Tokens` class). Listing the engine's real names keeps the legend single-sourced with what
 * the engine actually resolves.
 */
// This is a Vite *library* bundle (one ESM artifact), not an HMR app, so the fast-refresh
// "components-only file" rule's premise does not apply. The `TOKENS` table is intentionally
// co-located with the component that renders it; splitting it out would be pure churn.
/* eslint-disable react-refresh/only-export-components */
import { Chip } from "./primitives";

/**
 * A legend entry. `kind` drives insertion style; `insert` is the EXACT string spliced at the
 * caret when the chip is clicked.
 *
 *  - `core` tokens (`$title`, `$ext`) are effectively always-present, so they insert BARE.
 *  - `optional` tokens insert PRE-WRAPPED in one `{}` group whose leading separator + literals
 *    live INSIDE the group, so the whole span collapses (engine `RenderGroup`) when the token
 *    resolves empty — no dangling `[]`, no stray separator. Spec-like tokens use the bracket
 *    style `{ [$token]}`; prose-like tokens use the dash style `{ - $token}`. NB: bare `$token`
 *    only — the engine has NO `${token}` form.
 */
export interface TokenEntry {
  token: string;
  label: string;
  kind: "core" | "optional";
  insert: string;
}

/** Canonical token names + short labels, in TemplateEngine.cs `Tokens` declaration order. */
export const TOKENS: readonly TokenEntry[] = [
  { token: "$title", label: "Title", kind: "core", insert: "$title" },
  { token: "$studio", label: "Studio", kind: "optional", insert: "{ - $studio}" },
  { token: "$studioCode", label: "Studio code", kind: "optional", insert: "{ - $studioCode}" },
  { token: "$date", label: "Date", kind: "optional", insert: "{ - $date}" },
  { token: "$year", label: "Year", kind: "optional", insert: "{ [$year]}" },
  { token: "$height", label: "Height", kind: "optional", insert: "{ [$height]}" },
  { token: "$width", label: "Width", kind: "optional", insert: "{ [$width]}" },
  {
    token: "$resolution",
    label: "Resolution (e.g. 1080p)",
    kind: "optional",
    insert: "{ [$resolution]}",
  },
  { token: "$videoCodec", label: "Video codec", kind: "optional", insert: "{ [$videoCodec]}" },
  { token: "$audioCodec", label: "Audio codec", kind: "optional", insert: "{ [$audioCodec]}" },
  { token: "$frameRate", label: "Frame rate", kind: "optional", insert: "{ [$frameRate]}" },
  { token: "$duration", label: "Duration", kind: "optional", insert: "{ [$duration]}" },
  { token: "$performers", label: "Performers", kind: "optional", insert: "{ - $performers}" },
  { token: "$tags", label: "Tags", kind: "optional", insert: "{ - $tags}" },
  { token: "$ext", label: "Extension", kind: "core", insert: "$ext" },
];

/** Tooltip copy for an optional chip — names the exact wrapped string it inserts. */
function optionalTooltip(t: TokenEntry): string {
  return `Inserts wrapped in an optional group: ${t.insert} — disappears cleanly when empty.`;
}

export function TokenLegend({ onInsert }: { onInsert: (token: string) => void }) {
  return (
    <div>
      <p className="mb-1 text-xs text-muted">
        Click a token to insert it. <span className="text-foreground">Optional tokens</span> (marked{" "}
        <span className="font-mono">{"{ }"}</span>) insert wrapped so they vanish — with their
        punctuation — when empty. <span className="text-foreground">Core tokens</span> insert as-is.
      </p>
      <div className="flex flex-wrap gap-1">
        {TOKENS.map((t) => (
          <Chip
            key={t.token}
            selected={false}
            mono
            title={t.kind === "optional" ? optionalTooltip(t) : t.label}
            onClick={() => {
              onInsert(t.insert);
            }}
          >
            {t.token}
            {t.kind === "optional" ? <span className="ml-1 text-muted">{"{ }"}</span> : null}
          </Chip>
        ))}
      </div>
    </div>
  );
}
