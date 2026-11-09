/**
 * The Renamer settings page. The host renders its tab with page layout and no card around it, so this
 * owns the whole canvas: a stack of titled cards, the undo footer and a fixed save bar. It holds only
 * cross-section glue: the template inputs a token chip inserts into, and the empty-sample advisory
 * from the live preview.
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
import { useLibraryPaths } from "./useLibraryPaths";
import { AdvancedSection } from "./AdvancedSection";
import { canSave, useRenamerOptions, type SaveBarState } from "./useRenamerOptions";
import { useRenamePreview } from "./useRenamePreview";
import { useRenameLibrary } from "./useRenameLibrary";

// The error dot takes its colour from an inline style, so its class carries none.
function dotClass(state: SaveBarState): string {
  if (state.kind === "failed") return "";
  return state.kind === "saved" ? "bg-green-400" : "bg-amber-400";
}

function Headline({ title, detail }: Readonly<{ title: string; detail: string }>) {
  return (
    <>
      <div className="text-sm font-semibold text-foreground">{title}</div>
      <div className="mt-0.5 text-xs text-secondary">{detail}</div>
    </>
  );
}

function SaveBarMessage({ state }: Readonly<{ state: SaveBarState }>) {
  switch (state.kind) {
    case "failed":
      return (
        <StatusText kind="error">
          Couldn't save settings: {state.detail}. Your changes are still here; try Save again.
        </StatusText>
      );
    case "saved":
      // The reason the read failed is on the notice above the panel.
      return (
        <StatusText kind="success">
          {state.unconfirmed
            ? "Settings saved. The page couldn't read them back, so what's shown is what was sent."
            : "Settings saved."}
        </StatusText>
      );
    case "recovered":
      return (
        <Headline
          title="Recovered settings not stored yet"
          detail="Your saved settings couldn't be read. Save to replace them with the defaults shown."
        />
      );
    case "blocked":
      return (
        <Headline
          title="Saving is paused"
          detail={`${
            state.migration === "name"
              ? "Your tag and performer rules are"
              : "Your destination folders are"
          } waiting for a one-time conversion. Restart Cove, then reload this page.`}
        />
      );
    case "unsaved":
      return <Headline title="Unsaved changes" detail="Nothing on disk changes until you save." />;
  }
}

function SaveBar({
  state,
  saving,
  onSave,
  onDiscard,
}: Readonly<{
  state: SaveBarState | null;
  saving: boolean;
  onSave: () => void;
  onDiscard: () => void;
}>) {
  if (state === null) return null;
  return (
    <div className="pointer-events-none fixed inset-x-0 bottom-0 z-50 flex justify-center px-4 py-4">
      {/* py-3.5 is host-absent (Cove's own UI never uses it, so its prebuilt bundle omits it);
          inline the 0.875rem padding instead of shipping CSS. */}
      <div
        className="pointer-events-auto flex w-full max-w-3xl items-center gap-4 rounded-2xl border border-border bg-card px-5 shadow-lg"
        style={{ paddingTop: "0.875rem", paddingBottom: "0.875rem" }}
      >
        {/* Cove's own UI writes the red-400 fill only at 80% alpha, so its prebuilt stylesheet omits
            the full-strength utility and the error dot resolved to no fill. The colour scale's custom
            property is declared whether or not a utility using it is, so inline the tone off that. */}
        <span
          className={`h-2 w-2 shrink-0 rounded-full ${dotClass(state)}`}
          style={state.kind === "failed" ? { backgroundColor: "var(--color-red-400)" } : undefined}
        />
        <div className="min-w-0 flex-1">
          <SaveBarMessage state={state} />
        </div>
        <div className="flex shrink-0 items-center gap-3">
          {/* Recovery is the one state with no edit and no message behind it, so there is nothing
              for Discard to take back. */}
          {state.kind === "recovered" ? null : (
            <Button variant="ghost" onClick={onDiscard} disabled={saving}>
              Discard
            </Button>
          )}
          <Button onClick={onSave} disabled={!canSave(state) || saving}>
            {saving ? <Spinner /> : null}
            Save changes
          </Button>
        </div>
      </div>
    </div>
  );
}

export function RenamePage() {
  const {
    options,
    loading,
    loadError,
    saving,
    saveBar,
    recoveredFromBadBlob,
    pendingNameMigration,
    pendingDestinationMigration,
    dirty,
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
    renameProgress,
    renameLibrary,
  } = useRenameLibrary();
  const library = useLibraryPaths();

  // Last-focused template input, so a token chip inserts at its caret.
  const filenameRef = useRef<HTMLInputElement>(null);
  const folderRef = useRef<HTMLInputElement>(null);
  const activeTemplateRef = useRef<"filename" | "folder">("filename");

  // Only while there is nothing to edit. A later read can fail with a document still in hand, and
  // blanking the panel there takes the user's unsaved edits off screen.
  if (loadError && options === null) {
    return (
      <div className="space-y-3">
        <StatusText kind="error">Couldn't load your saved settings: {loadError}. Retry.</StatusText>
        <div>
          <Button variant="ghost" onClick={() => void load()}>
            Retry
          </Button>
        </div>
      </div>
    );
  }

  if (loading || options === null) {
    return (
      <div className="flex items-center gap-2 text-sm text-secondary">
        <Spinner />
        Loading settings…
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

  // Read off the debounced /preview-sample result already loaded, so the advisory costs no request.
  const emptySamples = (preview ?? [])
    .filter((r) => r.flags.includes("empty"))
    .map((r) => r.sampleLabel);

  // pb-20 (5rem bottom clearance for the sticky save bar) is host-absent - inline it.
  return (
    <div className="space-y-6" style={saveBar ? { paddingBottom: "5rem" } : undefined}>
      {loadError ? (
        <StatusText kind="error">
          Couldn&apos;t re-read your saved settings: {loadError}. What&apos;s shown is the last copy
          this page has.
        </StatusText>
      ) : null}

      {/* Every other panel renders as a full-width sibling below this grid, so the preview's sticky
          containing block is that column's height, not the whole page. Standard grid-cols-3 and
          col-span-2 only: the host stylesheet carries no arbitrary [..] values. */}
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
          onRenameAll={() => void renameLibrary()}
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

      <UndoSection />

      {/* The bar and the dialog are both fixed at the same layer, and this one is the later
          sibling, so with the dialog open the bar would paint over it. The page keeps its clearance
          meanwhile, because the page behind the dialog must not shift under an open overlay. */}
      <SaveBar
        state={dryRunOpen ? null : saveBar}
        saving={saving}
        onSave={() => void onSave()}
        onDiscard={discard}
      />
    </div>
  );
}
