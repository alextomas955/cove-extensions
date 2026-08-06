/**
 * RenameSettingsPanel — the extension's settings + live-preview page, as a composition root.
 *
 * Rendered by the host with NO props inside its own SectionCard, so the panel ROOT is a plain
 * <div> — no outer card, no page title. The data layer lives in three R9 hooks (useRenamerOptions
 * for load/save, useRenamePreview for the debounced /preview-sample fetch, useRenameLibrary for the
 * scan+rename-library job); this body wires those hooks to the presentational per-section children
 * (FilenameSection, LivePreviewPane, WhatGetsRenamedSection, RunAutomationSection,
 * TokenSettingsSection, DestinationRoutingSection, AdvancedSection) plus the DryRunModal, UndoSection,
 * and the fixed save bar. It owns only cross-section glue: the template-input refs used for at-caret
 * token insertion, and the empty-sample advisory derived from the live preview.
 */
import { useRef } from "react";

import { Button, StatusText, Spinner } from "@cove-extensions/ui-shared";
import { UndoSection } from "./UndoSection";
import { DryRunModal } from "./dry-run/DryRunModal";
import { FilenameSection } from "./FilenameSection";
import { LivePreviewPane } from "./LivePreviewPane";
import { WhatGetsRenamedSection } from "./WhatGetsRenamedSection";
import { RunAutomationSection } from "./RunAutomationSection";
import { TokenSettingsSection } from "./TokenSettingsSection";
import { DestinationRoutingSection } from "./DestinationRoutingSection";
import { AdvancedSection } from "./AdvancedSection";
import { useRenamerOptions } from "./useRenamerOptions";
import { useRenamePreview } from "./useRenamePreview";
import { useRenameLibrary } from "./useRenameLibrary";

/**
 * The fixed-bottom global save bar — reachable from anywhere on the page, visible only while
 * `dirty`. Reuses the dirty/saving/saveError/savedFlash state and onSave handler from
 * useRenamerOptions; Discard reverts to the last-saved snapshot, never the factory defaults.
 */
function SaveBar({
  dirty,
  saving,
  saveError,
  savedFlash,
  canSave,
  onSave,
  onDiscard,
}: {
  dirty: boolean;
  saving: boolean;
  saveError: string | null;
  savedFlash: boolean;
  canSave: boolean;
  onSave: () => void;
  onDiscard: () => void;
}) {
  if (!dirty) return null;
  return (
    <div className="pointer-events-none fixed inset-x-0 bottom-0 z-50 flex justify-center px-4 py-4">
      {/* py-3.5 is host-absent (Cove's own UI never uses it, so its prebuilt bundle omits it);
          inline the 0.875rem padding instead of shipping CSS. */}
      <div
        className="pointer-events-auto flex w-full max-w-3xl items-center gap-4 rounded-2xl border border-border bg-card px-5 shadow-lg"
        style={{ paddingTop: "0.875rem", paddingBottom: "0.875rem" }}
      >
        {/* The error dot's `bg-red-400` is host-absent (Cove's stylesheet carries red-400 only at
            partial alpha), so it drew no fill; inline it from the theme variable. The saved and
            dirty dots are host-emitted classes and stay as they are. */}
        <span
          className={`h-2 w-2 shrink-0 rounded-full ${
            saveError ? "" : savedFlash ? "bg-green-400" : "bg-amber-400"
          }`}
          style={saveError ? { backgroundColor: "var(--color-red-400)" } : undefined}
        />
        <div className="min-w-0 flex-1">
          {saveError ? (
            <StatusText kind="error">
              Couldn't save settings — {saveError}. Your changes are still here; try Save again.
            </StatusText>
          ) : savedFlash ? (
            <StatusText kind="success">Settings saved.</StatusText>
          ) : (
            <>
              <div className="text-sm font-semibold text-foreground">Unsaved changes</div>
              <div className="mt-0.5 text-xs text-secondary">
                Nothing on disk changes until you save. Running a rename requires saving first.
              </div>
            </>
          )}
        </div>
        <div className="flex shrink-0 items-center gap-3">
          <Button variant="ghost" onClick={onDiscard} disabled={saving}>
            Discard
          </Button>
          <Button onClick={onSave} disabled={!canSave || saving}>
            {saving ? <Spinner /> : null}
            Save changes
          </Button>
        </div>
      </div>
    </div>
  );
}

/**
 * RenamePanelBody — the composition root rendered by the dedicated nav page (`RenamePage`). The root
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

  // Last-focused template input, so a token chip inserts at its caret.
  const filenameRef = useRef<HTMLInputElement>(null);
  const folderRef = useRef<HTMLInputElement>(null);
  const activeTemplateRef = useRef<"filename" | "folder">("filename");

  function insertToken(token: string) {
    const which = activeTemplateRef.current;
    const el = which === "folder" ? folderRef.current : filenameRef.current;
    const key: "FilenameTemplate" | "FolderTemplate" =
      which === "folder" ? "FolderTemplate" : "FilenameTemplate";
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
  }

  if (loading) {
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
          Couldn't load your saved settings — {loadError}. Retry, or continue with defaults below.
        </StatusText>
        <div>
          <Button variant="ghost" onClick={() => void load()}>
            Retry
          </Button>
        </div>
      </div>
    );
  }

  // Empty-for-sample advisory: read the existing debounced /preview-sample
  // result; name each sample whose flags include "empty". No new request.
  const emptySamples = (preview ?? [])
    .filter((r) => r.flags.includes("empty"))
    .map((r) => r.sampleLabel);

  // pb-20 (5rem bottom clearance for the sticky save bar) is host-absent — inline it.
  return (
    <div className="space-y-6" style={dirty ? { paddingBottom: "5rem" } : undefined}>
      {/* Two-pane shell, narrowed to the Filename & destination card only: the panel (2/3 via
        col-span-2) + the live preview (1/3) sticky on lg+. The other 5 panels render as full-width
        siblings below this grid, so the preview's sticky containing block is that first card's own
        height, not the whole page. Standard grid-cols-3 + col-span-2 only — the host Tailwind never
        compiles arbitrary [..] values for this bundle (verified live; nothing checks this
        automatically, so weigh any new utility against the host stylesheet by hand). */}
      <div className="grid grid-cols-1 gap-6 lg:grid-cols-3">
        <FilenameSection
          options={options}
          set={set}
          insertToken={insertToken}
          filenameRef={filenameRef}
          folderRef={folderRef}
          activeTemplateRef={activeTemplateRef}
          emptySamples={emptySamples}
          recoveredFromBadBlob={recoveredFromBadBlob}
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

      <DestinationRoutingSection options={options} set={set} />

      <AdvancedSection options={options} set={set} />

      {/* ── UNDO — the action surface, distinct from configuration, at the bottom. ── */}
      <div id="rename-undo-section">
        <UndoSection refreshKey={undoRefreshKey} />
      </div>

      <SaveBar
        dirty={dirty}
        saving={saving}
        saveError={saveError}
        savedFlash={savedFlash}
        canSave={canSave}
        onSave={() => void onSave()}
        onDiscard={discard}
      />
    </div>
  );
}
