/**
 * FilenameSection — the "Filename & folder" card: presets, the filename/folder template inputs with
 * their at-caret token insertion, inline template validation, and the token legend. Presentational
 * only — every edit flows up through the `set`/`insertToken` callbacks the panel threads in from
 * useRenamerOptions. Owns the `col-span-2` grid cell (with the bad-blob recovery banner) so it can
 * sit beside the sticky live-preview column.
 */
import type { Ref, RefObject } from "react";

import { type RenamerOptions, type LibraryPathsState } from "./options";
import {
  Field,
  TextInput,
  Subsection,
  SectionCard,
  Chip,
  StatusText,
} from "@cove-extensions/ui-shared";
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
          description="The destination for an item no rule matched: a library path, plus the folders made under it."
        >
          <DestinationField
            rootHelper="Which of Cove's library paths this destination measures from."
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
        </Subsection>
      </SectionCard>
    </div>
  );
}
