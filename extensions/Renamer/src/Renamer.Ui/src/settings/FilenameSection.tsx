/**
 * FilenameSection — the "Filename & folder" card: presets, the filename/folder template inputs with
 * their at-caret token insertion, inline template validation, and the token legend. Presentational
 * only — every edit flows up through the `set`/`insertToken` callbacks the panel threads in from
 * useRenamerOptions. Owns the `col-span-2` grid cell (with the bad-blob recovery banner) so it can
 * sit beside the sticky live-preview column.
 */
import type { Ref, RefObject } from "react";

import { type RenamerOptions } from "./options";
import {
  Field,
  TextInput,
  Subsection,
  SectionCard,
  Chip,
  StatusText,
} from "@cove-extensions/ui-shared";
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
      <p className="mt-1 text-xs text-muted">
        Click a preset to fill the filename template. You can edit it afterwards.
      </p>
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
  pendingNameMigration: boolean;
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

      <SectionCard
        title="Filename & folder"
        description="The name each file gets, and where it lands."
      >
        <Subsection
          title="Filename"
          description="Pick a preset or write your own. Click a token to insert it."
        >
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
        </Subsection>
        <Subsection
          title="Where files go"
          description="Folder path template — moves files on rename."
        >
          <Field
            label="Folder template"
            helper="Blank = no folder move (rename in place). Use / for sub-folders, e.g. $studio / $year."
          >
            <TextInput
              value={options.FolderTemplate}
              onChange={(v) => {
                set("FolderTemplate", v);
              }}
              onFocus={() => (activeTemplateRef.current = "folder")}
              inputRef={folderRef}
              mono
              placeholder="$studio / $year"
            />
          </Field>
          <TemplateValidation value={options.FolderTemplate} />
        </Subsection>
      </SectionCard>
    </div>
  );
}
