/**
 * FilenameSection — the two naming cards, "Filename" and "Where files go": presets, the
 * filename/folder template inputs with their at-caret token insertion, inline template validation,
 * and the token legend. Presentational only — every edit flows up through the `set`/`insertToken`
 * callbacks the panel threads in from useRenamerOptions. Owns the `col-span-2` grid cell (with the
 * bad-blob recovery banner) so both cards sit beside the sticky live-preview column.
 */
import type { Ref, RefObject } from "react";

import { type RenamerOptions, type LibraryPathsState } from "./options";
import { Field, TextInput, SectionCard, Chip, StatusText } from "@cove-extensions/ui-shared";
import { DestinationField } from "./DestinationField";
import { TokenLegend } from "./TokenLegend";
import { TemplateValidation } from "./templateAdvisories";
import { PRESETS } from "./presets";

/**
 * One-click starter templates. Each chip sets FilenameTemplate via the parent's
 * set() path so `dirty` flips and the existing debounced live preview re-renders — no toast, no
 * confirm. Chips reuse the legend-chip class (prose labels drop font-mono). Every preset label is a
 * React text node (auto-escaped); the templates come from the static PRESETS list.
 */
function PresetRow({ onApply }: { onApply: (filenameTemplate: string) => void }) {
  return (
    <div>
      <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-muted">
        Presets
      </span>
      <div className="flex flex-wrap gap-1">
        {PRESETS.map((p) => (
          <Chip
            key={p.label}
            selected={false}
            title={p.filenameTemplate}
            onClick={() => {
              onApply(p.filenameTemplate);
            }}
          >
            {p.label}
          </Chip>
        ))}
      </div>
    </div>
  );
}

export interface FilenameSectionProps {
  options: RenamerOptions;
  set: <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => void;
  insertToken: (token: string) => void;
  filenameRef: Ref<HTMLInputElement>;
  folderRef: Ref<HTMLInputElement>;
  activeTemplateRef: RefObject<"filename" | "folder">;
  emptySamples: string[];
  recoveredFromBadBlob: boolean;
  /** Save is refused for the stored blob's sake, and the banner below is where a user learns why. */
  pendingNameMigration: boolean;
  /** The same refusal for the destination half, which waits on a different thing and says so. */
  pendingDestinationMigration: boolean;
  /** Cove's library paths, so the default destination offers them as choices. */
  library: LibraryPathsState;
}

export function FilenameSection({
  options,
  set,
  insertToken,
  filenameRef,
  folderRef,
  activeTemplateRef,
  emptySamples,
  recoveredFromBadBlob,
  pendingNameMigration,
  pendingDestinationMigration,
  library,
}: FilenameSectionProps) {
  return (
    <div className="col-span-2 space-y-6">
      {recoveredFromBadBlob ? (
        <StatusText kind="error">
          Your saved settings couldn't be read and have been reset to defaults. Review the options
          below and save to store a clean copy.
        </StatusText>
      ) : null}

      {pendingNameMigration ? (
        <StatusText kind="error">
          Your tag and performer rules are still stored by name and are waiting for a one-time
          conversion that runs when Cove starts. Saving is disabled until then, because this page
          can't show those rules and would replace them. Restart Cove, then reload this page.
        </StatusText>
      ) : null}

      {pendingDestinationMigration ? (
        <StatusText kind="error">
          Your destination folders are still stored as plain paths and are waiting for a one-time
          conversion that runs when Cove starts. Saving is disabled until then, because this page
          can't show those folders and would replace them. The conversion needs at least one library
          path configured in Cove; add one if there is none, then restart Cove and reload this page.
        </StatusText>
      ) : null}

      <SectionCard title="Filename" description="Pick a preset or write your own.">
        <PresetRow
          onApply={(t) => {
            set("FilenameTemplate", t);
          }}
        />
        <Field label="Filename template">
          <TextInput
            value={options.FilenameTemplate}
            onChange={(v) => {
              set("FilenameTemplate", v);
            }}
            onFocus={() => (activeTemplateRef.current = "filename")}
            inputRef={filenameRef}
            mono
            placeholder="$title"
          />
        </Field>
        <TemplateValidation value={options.FilenameTemplate} emptySamples={emptySamples} />
        <TokenLegend onInsert={insertToken} />
      </SectionCard>

      <SectionCard
        title="Where files go"
        description="The destination for an item no rule matched."
      >
        <DestinationField
          value={{ Root: options.FolderRoot, Template: options.FolderTemplate }}
          onChange={(destination) => {
            set("FolderRoot", destination.Root);
            set("FolderTemplate", destination.Template);
          }}
          library={library}
          helper="Blank = no folder move (rename in place). Use / for sub-folders, e.g. $studio/$year."
          templateRef={folderRef}
          onTemplateFocus={() => (activeTemplateRef.current = "folder")}
        />
        <TemplateValidation value={options.FolderTemplate} />
      </SectionCard>
    </div>
  );
}
