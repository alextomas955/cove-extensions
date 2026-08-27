/**
 * RunAutomationSection — the "Run & automation" card: the auto-rename-on-update toggle plus the
 * "Run for the whole library" block (Dry run / Rename all buttons, the unsaved-edits warning, and
 * the run-result banner with its jump-to-Undo link). Presentational: the actual scan+rename job and
 * its feedback live in useRenameLibrary; this only renders state and calls the passed-in handlers.
 */
import { AlertTriangle } from "lucide-react";

import { type RenamerOptions } from "./options";
import { Toggle, SectionCard, Button, StatusText, Spinner } from "@cove-extensions/ui-shared";
import { type RunLibraryFeedback } from "./useRenameLibrary";

export interface RunAutomationSectionProps {
  options: RenamerOptions;
  set: <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => void;
  dirty: boolean;
  renamingLibrary: boolean;
  runLibraryFeedback: RunLibraryFeedback;
  onDryRun: () => void;
  onRenameAll: () => void;
}

export function RunAutomationSection({
  options,
  set,
  dirty,
  renamingLibrary,
  runLibraryFeedback,
  onDryRun,
  onRenameAll,
}: RunAutomationSectionProps) {
  return (
    <SectionCard
      title="Run & automation"
      description="When renames happen — on metadata update, or on demand."
    >
      <Toggle
        label="Auto-rename on update"
        checked={options.AutoRenamerOnUpdate}
        onChange={(v) => {
          set("AutoRenamerOnUpdate", v);
        }}
        helper="Renames an item whenever its metadata changes."
      />
      <div className="border-t border-border pt-4">
        <div className="flex flex-wrap items-start gap-4">
          <div className="min-w-0 flex-1">
            <div className="text-base font-semibold text-foreground">Run for the whole library</div>
            <p className="mt-1 text-sm text-secondary">
              Applies your rules to every matching item. A dry run writes nothing.
            </p>
          </div>
          <div className="flex shrink-0 flex-wrap items-center gap-3">
            <Button variant="ghost" onClick={onDryRun}>
              Dry run
            </Button>
            <Button onClick={onRenameAll} disabled={dirty || renamingLibrary}>
              {renamingLibrary ? <Spinner /> : null}
              Rename all files
            </Button>
          </div>
        </div>
        {dirty ? (
          <p
            className="mt-2 flex items-start gap-1 text-xs text-amber-400"
            role="status"
            aria-live="polite"
          >
            <AlertTriangle className="h-3 w-3 shrink-0" />
            <span>
              Dry run previews your edits before saving. Save before &ldquo;Rename all files&rdquo;
              to run them for real.
            </span>
          </p>
        ) : null}
        {runLibraryFeedback ? (
          <p className="mt-2">
            <StatusText kind={runLibraryFeedback.kind}>
              {runLibraryFeedback.kind === "success" ? "✓ " : ""}
              {runLibraryFeedback.text}
              {runLibraryFeedback.kind === "success" ? (
                <>
                  {" "}
                  <button
                    type="button"
                    onClick={() => {
                      document
                        .getElementById("rename-undo-section")
                        ?.scrollIntoView({ behavior: "smooth" });
                    }}
                    // No underline: hover:no-underline is host-absent (and hover:underline
                    // is too), and an inline style can't express a :hover state. The accent
                    // color alone marks it as the actionable control.
                    className="text-accent"
                  >
                    Undo
                  </button>
                </>
              ) : null}
            </StatusText>
          </p>
        ) : null}
      </div>
    </SectionCard>
  );
}
