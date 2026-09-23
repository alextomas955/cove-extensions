import { SaveBar, type SaveOutcome } from "@cove-extensions/ui-shared";

import { useNow } from "../common/lib/useNow";
import { deriveAsyncRegionState } from "../common/ui/asyncRegionLogic";
import { RefusalNotice } from "../common/ui/RefusalNotice";
import { ConnectionSection } from "./ConnectionSection";
import { FolderAgreementSection } from "./FolderAgreementSection";
import { ImportBanner } from "./ImportBanner";
import { ImportBehaviorSection } from "./ImportBehaviorSection";
import { ImportWebhookSection } from "./ImportWebhookSection";
import { SyncLibrarySection } from "./SyncLibrarySection";
import { testsStoredConnection, unsavedFields, unsavedSummary } from "./settingsDraftLogic";
import { syncSentences } from "./syncLibraryLogic";
import { useSettingsDraft } from "./useSettingsDraft";
import type { SaveState } from "./settingsDraftStore";
import { useFolderAgreement } from "./useFolderAgreement";
import { useImportBanner } from "./useImportBanner";
import { useSyncLibrary } from "./useSyncLibrary";
import { useRegistration } from "./useRegistration";

/**
 * The component the host mounts inside the "Whisparr Sync" settings tab.
 *
 * The host draws the tab header from the manifest and adds no card chrome. This component must add
 * no outer page heading and no page gutter, or the tab name is drawn twice.
 *
 * The host passes `{ onNavigate }`; this surface does not navigate and ignores it. Styling is host
 * Tailwind token classes only, because the host's Tailwind JIT never scans this bundle.
 */
export function WhisparrSyncPage() {
  const {
    state,
    editAddress,
    editKey,
    clearStoredKey,
    editBehavior,
    chooseGeneration,
    discard,
    test,
    save,
  } = useSettingsDraft(reloadPage);
  const registration = useRegistration();
  const banner = useImportBanner();
  const agreement = useFolderAgreement();
  const sync = useSyncLibrary();
  const unsaved = unsavedFields(state.settings, state.draft);
  const now = useNow();

  // One reason stated once, rather than the same sentence beside each control that shares it.
  const sharedReason = state.settings === null ? reasonNothingIsReadable(state.readError) : null;

  return (
    // The bar is fixed to the viewport and covers the foot of the page, and the host's padding
    // scale stops short of its height. The gutter is always present: one that appeared with the
    // bar would move the page under the pointer at the first edit.
    <div className="space-y-4" style={{ paddingBottom: BAR_GUTTER }}>
      <ImportBanner read={banner.read} view={banner.view} now={now} />

      <FolderAgreementSection
        read={agreement.read}
        view={agreement.view}
        drafts={agreement.drafts}
        saving={agreement.saving}
        answers={agreement.answers}
        onPathChange={agreement.editPath}
        onSave={agreement.save}
        onWithdraw={agreement.withdraw}
      />

      {sharedReason === null ? null : (
        <RefusalNotice reason={sharedReason} affectedControls={SHARED_REASON_CONTROLS} />
      )}

      <ConnectionSection
        card={state.draft.generation}
        settings={state.settings}
        readFailed={state.read.failed}
        draft={state.draft}
        test={state.test}
        testsStored={testsStoredConnection(state.settings, state.draft)}
        sharedReason={sharedReason}
        now={now}
        onAddressChange={editAddress}
        onKeyChange={editKey}
        onClearStoredKey={clearStoredKey}
        onChooseGeneration={chooseGeneration}
        onTest={test}
      />

      <ImportWebhookSection
        view={registration.view}
        readFailed={registration.readFailed}
        address={registration.address}
        registering={registration.registering}
        registerError={registration.registerError}
        copyResult={registration.copyResult}
        sharedReason={sharedReason}
        onAddressChange={registration.editAddress}
        onCopy={registration.copy}
        onRegister={registration.register}
      />

      <ImportBehaviorSection
        behavior={state.draft.upgradeBehavior}
        sharedReason={sharedReason}
        onChange={editBehavior}
      />

      <SyncLibrarySection
        counts={sync.read?.view ?? null}
        preview={deriveAsyncRegionState(sync.preview)}
        counting={sync.counting}
        now={now}
        onCount={sync.count}
        sharedReason={sharedReason}
        noConnection={sync.read?.refusal === "noInstanceConnected"}
        syncRunning={sync.syncRunning}
        starting={sync.starting}
        started={sync.started}
        refused={sync.refused}
        monitorAlso={sync.monitorAlso}
        sentences={syncSentences(sync.read?.view?.registers ?? null)}
        onMonitorAlso={sync.chooseMonitorAlso}
        onSync={sync.sync}
      />

      <SaveBar
        dirty={unsaved.length > 0}
        saving={state.save.status === "saving"}
        canSave={state.settings !== null}
        summary={
          <>
            <div className="text-sm font-semibold text-foreground">Unsaved changes</div>
            <div className="mt-0.5 text-xs text-secondary">{unsavedSummary(unsaved)}</div>
          </>
        }
        outcome={saveOutcome(state.save)}
        onSave={save}
        onDiscard={discard}
      />
    </div>
  );
}

// The bar, including the wrapper that holds it off the foot of the viewport.
const BAR_GUTTER = "96px";

function saveOutcome(save: SaveState): SaveOutcome {
  if (save.status === "failed") {
    return { kind: "failed", message: `Cove could not save: ${save.message}` };
  }
  return save.status === "saved" ? { kind: "saved", message: "Settings saved." } : { kind: "none" };
}

// The controls the shared reason disables: connection test, registration, replacement-file
// behaviour, library sync. The bar's save control is not one of them: nothing can be unsaved
// before the settings have arrived, so the bar is not drawn while the reason stands.
const SHARED_REASON_CONTROLS = 4;

function reasonNothingIsReadable(readError: string | null): string {
  return readError === null
    ? "Cove is still reading the stored connection."
    : "Cove could not read the stored connection, so nothing here can act on it yet.";
}

// A generation change reloads the page rather than re-reading each surface: every surface reads
// the connected generation's capabilities, and no single place knows to tell them all.
// At module scope so the hook that calls it does not see a new function on each render.
function reloadPage() {
  window.location.reload();
}
