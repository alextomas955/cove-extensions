/**
 * Always-visible token reference. Clicking a chip inserts `$token` at the caret of the
 * last-focused template input (filename or folder — the panel passes the active ref). A chip is
 * tinted when either template already uses its token.
 */
import { Chip } from "@cove-extensions/ui-shared";

import { templateUsesToken } from "./templateValidation";
import { TOKENS, type TokenEntry } from "./tokens";

/** Tooltip copy for an optional chip — names the exact wrapped string it inserts. */
function optionalTooltip(t: TokenEntry): string {
  return `Inserts wrapped in an optional group: ${t.insert} — disappears cleanly when empty.`;
}

export function TokenLegend({
  onInsert,
  filenameTemplate,
  folderTemplate,
}: Readonly<{
  onInsert: (token: string) => void;
  filenameTemplate: string;
  folderTemplate: string;
}>) {
  return (
    <div>
      <p className="mb-1 text-xs text-muted">
        <span className="font-mono">{"{ }"}</span> tokens drop out with their punctuation when
        empty.
      </p>
      <div className="flex flex-wrap gap-1">
        {TOKENS.map((t) => (
          <Chip
            key={t.token}
            selected={templateUsesToken(t.token, filenameTemplate, folderTemplate)}
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
