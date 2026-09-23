/**
 * RenameSettingsPanel - the extension's settings + live-preview page, as a composition root.
 *
 * Rendered by the host with no props inside its own SectionCard, so the panel root is a plain
 * <div> - no outer card, no page title. The data layer lives in three R9 hooks (useRenamerOptions
 * for load/save, useRenamePreview for the debounced /preview-sample fetch, useRenameLibrary for the
 * scan+rename-library job); this body wires those hooks to the presentational per-section children
 * (FilenameSection, LivePreviewPane, WhatGetsRenamedSection, RunAutomationSection,
 * TokenSettingsSection, DestinationRoutingSection, AdvancedSection) plus the DryRunModal, UndoSection,
 * and the fixed save bar. It owns only cross-section glue: the template-input refs used for at-caret
 * token insertion, and the empty-sample advisory derived from the live preview.
 *
 * Every card is a direct child of the root stack and carries its own title. Undo closes the page as
 * a footer row rather than a card.
 */
import { useRef } from "react";

import { Button, SaveBar, Spinner, StatusText, type SaveOutcome } from "@cove-extensions/ui-shared";
import { UndoSection } from "./UndoSection";
import { DryRunModal } from "./dry-run/DryRunModal";
import { FilenameSection } from "./FilenameSection";
import { LivePreviewPane } from "./LivePreviewPane";
import { WhatGetsRenamedSection } from "./WhatGetsRenamedSection";
import { RunAutomationSection } from "./RunAutomationSection";
import { TokenSettingsSection } from "./TokenSettingsSection";
import { DestinationRoutingSection } from "./DestinationRoutingSection";
import { useLibraryPaths } from "./useLibraryPaths";
import { AdvancedSection } from "./AdvancedSection";
import { useRenamerOptions } from "./useRenamerOptions";
import { useRenamePreview } from "./useRenamePreview";
import { useRenameLibrary } from "./useRenameLibrary";

/**
 * RenamePanelBody - the composition root rendered by the dedicated nav page (`RenamePage`). The root
 * stays a plain `<div className="space-y-6">`; the host SectionCard / page wrapper supplies outer
 * chrome.
 */
export function RenamePanelBody() {
  const {
    options,
    loading,
    loadError,
    saving,
    saveError,
    savedFlash,
    recoveredFromBadBlob,
    pendingNameMigration,
    pendingDestinationMigration,
    dirty,
    canSave,
    load,
    onSave,
    discard,
    set,
    setMulti,
  } = useRenamerOptions();
  const { preview, previewError } = useRenamePreview(options, loading);
  const {
    dryRunOpen,
    setDryRunOpen,
    renamingLibrary,
    runLibraryFeedback,
    undoRefreshKey,
    renameProgress,
    renameLibrary,
  } = useRenameLibrary();
  const library = useLibraryPaths();

  // Last-focused template input, so a token chip inserts at its caret.
  const filenameRef = useRef<HTMLInputElement>(null);
  const folderRef = useRef<HTMLInputElement>(null);
  const activeTemplateRef = useRef<"filename" | "folder">("filename");

  if (loading || options === null) {
    return (
      <div className="flex items-center gap-2 text-sm text-secondary">
        <Spinner />
        Loading settings…
      </div>
    );
  }

  if (loadError) {
    return (
      <div className="space-y-3">
        <StatusText kind="error">
          Couldn't load your saved settings — {loadError}. Retry.
        </StatusText>
        <div>
          <Button variant="ghost" onClick={() => void load()}>
            Retry
          </Button>
        </div>
      </div>
    );
  }

  const insertToken = (token: string) => {
    const which = activeTemplateRef.current;
    const el = which === "folder" ? folderRef.current : filenameRef.current;
    const key: "filenameTemplate" | "folderTemplate" =
      which === "folder" ? "folderTemplate" : "filenameTemplate";
    const current = options[key];
    if (el && typeof el.selectionStart === "number") {
      const start = el.selectionStart;
      const end = el.selectionEnd ?? start;
      const next = current.slice(0, start) + token + current.slice(end);
      set(key, next);
      requestAnimationFrame(() => {
        el.focus();
        const caret = start + token.length;
        el.setSelectionRange(caret, caret);
      });
    } else {
      set(key, current + token);
    }
  };

  // Empty-for-sample advisory: read the existing debounced /preview-sample
  // result; name each sample whose flags include "empty". No new request.
  const emptySamples = (preview ?? [])
    .filter((r) => r.flags.includes("empty"))
    .map((r) => r.sampleLabel);

  // pb-20 (5rem bottom clearance for the sticky save bar) is host-absent - inline it.
  return (
    <div className="space-y-6" style={dirty ? { paddingBottom: "5rem" } : undefined}>
      {/* Two-pane shell, narrowed to the two naming cards: they take 2/3 via col-span-2, the live
          preview 1/3, sticky on lg+. Every other panel renders as a full-width sibling below this
          grid, so the preview's sticky containing block is that column's height, not the whole page.
          Standard grid-cols-3 + col-span-2 only — the host Tailwind never compiles arbitrary [..]
          values for this bundle (verified live). */}
      <div className="grid grid-cols-1 gap-6 lg:grid-cols-3">
        <FilenameSection
          library={library}
          options={options}
          set={set}
          insertToken={insertToken}
          filenameRef={filenameRef}
          folderRef={folderRef}
          activeTemplateRef={activeTemplateRef}
          emptySamples={emptySamples}
          recoveredFromBadBlob={recoveredFromBadBlob}
          pendingNameMigration={pendingNameMigration}
          pendingDestinationMigration={pendingDestinationMigration}
        />
        <LivePreviewPane preview={preview} previewError={previewError} />
      </div>

      <WhatGetsRenamedSection options={options} set={set} />

      <RunAutomationSection
        options={options}
        set={set}
        dirty={dirty}
        renamingLibrary={renamingLibrary}
        runLibraryFeedback={runLibraryFeedback}
        onDryRun={() => {
          setDryRunOpen(true);
        }}
        onRenameAll={() => void renameLibrary()}
      />

      {dryRunOpen ? (
        <DryRunModal
          options={options}
          dirty={dirty}
          onClose={() => {
            setDryRunOpen(false);
          }}
          onRenameAll={(counts) => void renameLibrary(counts)}
          renaming={renamingLibrary}
          renameProgress={renameProgress}
        />
      ) : null}

      <TokenSettingsSection
        options={options}
        set={set}
        setMulti={setMulti}
        insertToken={insertToken}
      />

      <DestinationRoutingSection options={options} set={set} library={library} />

      <AdvancedSection options={options} set={set} />

      <UndoSection refreshKey={undoRefreshKey} />

      {/* The bar and the dialog are both fixed at the same layer, and this one is the later sibling,
          so with the dialog open the bar paints over it and its buttons stay mouse-reachable. The
          root's bottom padding stays keyed on `dirty` alone: that clearance is for the page behind
          the dialog, which must not shift under an open overlay. */}
      <SaveBar
        dirty={dirty && !dryRunOpen}
        saving={saving}
        canSave={canSave}
        summary={
          <>
            <div className="text-sm font-semibold text-foreground">Unsaved changes</div>
            <div className="mt-0.5 text-xs text-secondary">
              Nothing on disk changes until you save.
            </div>
          </>
        }
        outcome={saveOutcome(saveError, savedFlash)}
        onSave={() => void onSave()}
        onDiscard={discard}
      />
    </div>
  );
}

function saveOutcome(saveError: string | null, savedFlash: boolean): SaveOutcome {
  if (saveError !== null) {
    return {
      kind: "failed",
      message: `Couldn't save settings — ${saveError}. Your changes are still here; try Save again.`,
    };
  }
  return savedFlash ? { kind: "saved", message: "Settings saved." } : { kind: "none" };
}
