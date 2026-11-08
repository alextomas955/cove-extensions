/**
 * Always-visible token reference. Clicking a chip inserts `$token` at the caret of the
 * last-focused template input (filename or folder - the panel passes the active ref). A chip is
 * tinted when either template already uses its token.
 */
import { Chip } from "@cove-extensions/ui-shared";

import { templateUsesToken } from "./templateLogic";
import { TOKENS, type TokenEntry } from "./tokens";

/** Tooltip copy for an optional chip - names the exact wrapped string it inserts. */
function optionalTooltip(t: TokenEntry): string {
  return `Inserts ${t.insert}, an optional group that disappears when empty.`;
}

/**
 * What a chip announces. A chip inserts its token whether or not the templates already use it, so it
 * is not a toggle and carries no pressed state; the tint is the only thing saying a token is already
 * in use, and a tint reaches nobody who cannot see it. `title` is the chip's accessible description,
 * so the same fact arrives there too.
 */
function describe(t: TokenEntry, used: boolean): string {
  const base = t.kind === "optional" ? optionalTooltip(t) : t.label;
  return used ? `${base} Already in your template.` : base;
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
        {TOKENS.map((t) => {
          const used = templateUsesToken(t.token, filenameTemplate, folderTemplate);
          return (
            <Chip
              key={t.token}
              selected={used}
              mono
              title={describe(t, used)}
              onClick={() => {
                onInsert(t.insert);
              }}
            >
              {t.token}
              {t.kind === "optional" ? <span className="ml-1 text-muted">{"{ }"}</span> : null}
            </Chip>
          );
        })}
      </div>
    </div>
  );
}
