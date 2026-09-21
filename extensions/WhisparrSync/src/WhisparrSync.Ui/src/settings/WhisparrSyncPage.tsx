import { useNow } from "../common/lib/useNow";
import { deriveAsyncRegionState } from "../common/ui/asyncRegionLogic";
import { RefusalNotice } from "../common/ui/RefusalNotice";
import { ConnectionSection } from "./ConnectionSection";
import { FolderAgreementSection } from "./FolderAgreementSection";
import { GenerationCards } from "./GenerationCards";
import { ImportBanner } from "./ImportBanner";
import { ImportBehaviorSection } from "./ImportBehaviorSection";
import { ImportWebhookSection } from "./ImportWebhookSection";
import { SyncLibrarySection } from "./SyncLibrarySection";
import { isNoOpSave, testsStoredConnection, valuesForCard } from "./connectLogic";
import { syncSentences } from "./syncLibraryLogic";
import { useConnection } from "./useConnection";
import { useFolderAgreement } from "./useFolderAgreement";
import { useImportBanner } from "./useImportBanner";
import { useImportBehavior } from "./useImportBehavior";
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
  const { state, editAddress, editKey, clearStoredKey, showCard, test, save } =
    useConnection(reloadPage);
  const registration = useRegistration();
  const banner = useImportBanner();
  const agreement = useFolderAgreement();
  const upgrade = useImportBehavior();
  const sync = useSyncLibrary();
  const stored = valuesForCard(state.settings, state.card);
  const now = useNow();

  // One reason stated once, rather than the same sentence beside each control that shares it.
  const sharedReason = state.settings === null ? reasonNothingIsReadable(state.readError) : null;

  return (
    <div className="space-y-4">
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

      <GenerationCards
        settings={state.settings}
        card={state.card}
        now={now}
        onShowCard={showCard}
      />

      <ConnectionSection
        card={state.card}
        stored={stored}
        readFailed={state.read.failed}
        draft={state.draft}
        test={state.test}
        save={state.save}
        noOpSave={isNoOpSave(
          stored,
          state.settings?.selectedGeneration ?? null,
          state.card,
          state.draft,
        )}
        testsStored={testsStoredConnection(
          stored,
          state.settings?.selectedGeneration ?? null,
          state.card,
          state.draft,
        )}
        sharedReason={sharedReason}
        now={now}
        onAddressChange={editAddress}
        onKeyChange={editKey}
        onClearStoredKey={clearStoredKey}
        onTest={test}
        onSave={save}
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
        behavior={upgrade.behavior}
        saving={upgrade.saving}
        saveError={upgrade.saveError}
        sharedReason={sharedReason}
        onChange={upgrade.choose}
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
    </div>
  );
}

// The controls the shared reason disables: connection test, connection save, registration, upgrade
// behaviour, library sync.
const SHARED_REASON_CONTROLS = 5;

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
