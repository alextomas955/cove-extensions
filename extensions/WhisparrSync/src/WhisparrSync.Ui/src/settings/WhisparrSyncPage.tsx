import { useNow } from "../common/lib/useNow";
import { RefusalNotice } from "../common/ui/RefusalNotice";
import { ConnectionSection } from "./ConnectionSection";
import { GenerationCards } from "./GenerationCards";
import { ImportBanner } from "./ImportBanner";
import { ImportBehaviorSection } from "./ImportBehaviorSection";
import { ImportWebhookSection } from "./ImportWebhookSection";
import { isNoOpSave, testsStoredConnection, valuesForCard } from "./connectLogic";
import { useConnection } from "./useConnection";
import { useImportBanner } from "./useImportBanner";
import { useImportBehavior } from "./useImportBehavior";
import { useRegistration } from "./useRegistration";

/**
 * The component the host mounts inside the "Whisparr Sync" settings tab.
 *
 * The tab uses the host's page layout, so the host draws the tab header from the manifest and no
 * card chrome around this component: the cards below are the extension's own. No outer page heading
 * and no page gutter, or the tab name would be drawn twice.
 *
 * The host passes `{ onNavigate }`; this surface does not navigate and ignores it. Styling is host
 * Tailwind token classes only, because the host's Tailwind JIT never scans this bundle.
 *
 * This component holds the data layer and hands each section what it renders, so a section can be
 * exercised with no host and no network.
 */
export function WhisparrSyncPage() {
  const { state, editAddress, editKey, clearStoredKey, showCard, test, save } =
    useConnection(reloadPage);
  const registration = useRegistration();
  const banner = useImportBanner();
  const upgrade = useImportBehavior();
  const stored = valuesForCard(state.settings, state.card);
  const now = useNow();

  // One reason, stated once, for the controls that share it - rather than the same sentence repeated
  // beside each of them.
  const sharedReason = state.settings === null ? reasonNothingIsReadable(state.readError) : null;

  return (
    <div className="space-y-4">
      <ImportBanner read={banner.read} view={banner.view} now={now} />

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
    </div>
  );
}

/**
 * How many controls the shared reason takes out while the settings are unreadable: the connection
 * test, the connection save, the registration, which acts on the stored connection rather than on the
 * form, and the upgrade behaviour, whose save would write over the settings that could not be read.
 */
const SHARED_REASON_CONTROLS = 4;

function reasonNothingIsReadable(readError: string | null): string {
  return readError === null
    ? "Cove is still reading the stored connection."
    : "Cove could not read the stored connection, so nothing here can act on it yet.";
}

/**
 * A generation change is applied by re-entering the page rather than by re-reading each surface,
 * because every surface reads the connected generation's capabilities and there is no single place
 * that would know to tell them all.
 *
 * Declared once at module scope so the hook that calls it does not see a new function on each render.
 */
function reloadPage() {
  window.location.reload();
}
