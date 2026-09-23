/**
 * The connection form: an address, a key, and the recorded lines from the instance's own answers.
 *
 * The API key travels in only. No prop can carry a stored key, so the pill reports presence and
 * never a value.
 */
import { Field, SectionCard, Spinner, StatusText, TextInput } from "@cove-extensions/ui-shared";

import type { WhisparrSyncGenerationSettingsView, WhisparrSyncSettingsView } from "../wire/api";
import { AsyncRegion } from "../common/ui/AsyncRegion";
import { OptionallyDisabled } from "../common/ui/DisabledControl";
import { deriveAsyncRegionState } from "../common/ui/asyncRegionLogic";
import { READ_IS_STALE } from "../common/ui/copy";
import { GenerationRow } from "./GenerationRow";
import { KeyStateField } from "./KeyStatePill";
import {
  describeRecorded,
  detectionOutcome,
  generationLabel,
  recordedRead,
  sentenceForKind,
  valuesForCard,
  valuesOf,
  type CardGeneration,
  type TransientTest,
} from "./connectLogic";
import type { SettingsDraft } from "./settingsDraftLogic";

/** The e2e specs locate the address field by this placeholder. */
const ADDRESS_PLACEHOLDER = "http://whisparr:6969";

export interface ConnectionSectionProps {
  /** The generation the draft holds, and so the one the fields below edit. */
  card: CardGeneration;
  settings: WhisparrSyncSettingsView | null;
  readFailed: boolean;
  draft: SettingsDraft;
  test: TransientTest;
  /** Whether Test asks about the stored connection rather than about the pair in the form. */
  testsStored: boolean;
  /** The one reason several controls on this page share, stated once by the page's own notice. */
  sharedReason: string | null;
  /** The instant the relative times are measured against. */
  now: number;
  onAddressChange: (next: string) => void;
  onKeyChange: (next: string) => void;
  onClearStoredKey: (cleared: boolean) => void;
  onChooseGeneration: (generation: CardGeneration) => void;
  onTest: () => void;
}

export function ConnectionSection({
  card,
  settings,
  readFailed,
  draft,
  test,
  testsStored,
  sharedReason,
  now,
  onAddressChange,
  onKeyChange,
  onClearStoredKey,
  onChooseGeneration,
  onTest,
}: ConnectionSectionProps) {
  const testing = test.phase === "running";
  const stored = valuesForCard(settings, card);

  // A stored key cannot be sent back, so testing a changed address needs a typed key. Testing the
  // address as stored does not, because that test asks about the stored connection.
  const testReason =
    sharedReason ??
    (testing
      ? "This test is still running."
      : draft.address.trim() === ""
        ? "Enter the Whisparr address first."
        : !testsStored && draft.apiKey === ""
          ? "Enter the Whisparr API key to test this address."
          : null);

  return (
    <SectionCard title="Connection" description="The Whisparr instance Cove keeps in step with.">
      <div className="space-y-4">
        <GenerationRow
          settings={settings}
          drafted={card}
          sharedReason={sharedReason}
          onChoose={onChooseGeneration}
        />

        <AsyncRegion
          state={deriveAsyncRegionState(recordedRead(stored, readFailed))}
          reading={<StatusText kind="muted">Reading the stored connection…</StatusText>}
          content={<RecordedLines stored={stored} now={now} />}
          empty={<RecordedLines stored={stored} now={now} />}
          outageNotice={<StatusText kind="error">{READ_IS_STALE}</StatusText>}
          failed={
            <StatusText kind="error">
              Cove could not read what is stored for this connection.
            </StatusText>
          }
        />

        <Field
          label="Whisparr address"
          labelStyle="mono"
          helper="The address Cove itself reaches Whisparr on, including the scheme and port."
        >
          {(id) => (
            <TextInput
              id={id}
              value={draft.address}
              onChange={onAddressChange}
              placeholder={ADDRESS_PLACEHOLDER}
            />
          )}
        </Field>

        <Field
          label="API key"
          labelStyle="mono"
          helper="Leave blank to keep the key already stored for this generation."
        >
          {(id) => (
            <KeyStateField
              id={id}
              value={draft.apiKey}
              storedKeyIsSet={stored === null ? null : stored.keyIsSet}
              onChange={onKeyChange}
            />
          )}
        </Field>

        <div className="flex items-center gap-3">
          <KeyIntent draft={draft} />
          {stored?.keyIsSet === true && !draft.keyCleared ? (
            <OptionallyDisabled
              name="Clear stored key"
              variant="ghost"
              reason={sharedReason}
              onClick={() => {
                onClearStoredKey(true);
              }}
            />
          ) : null}
          {draft.keyCleared ? (
            <OptionallyDisabled
              name="Keep stored key"
              variant="ghost"
              reason={sharedReason}
              onClick={() => {
                onClearStoredKey(false);
              }}
            />
          ) : null}
        </div>

        <div className="flex items-center gap-3" aria-busy={testing}>
          <OptionallyDisabled
            name={testing ? "Testing…" : "Test connection"}
            reason={testReason}
            onClick={onTest}
          />
          {testing ? <Spinner /> : null}
          <TestResult test={test} card={card} />
        </div>
      </div>
    </SectionCard>
  );
}

function RecordedLines({
  stored,
  now,
}: {
  stored: WhisparrSyncGenerationSettingsView | null;
  now: number;
}) {
  if (stored === null) {
    return null;
  }
  const lines = describeRecorded(stored, now);
  return (
    <div className="space-y-1">
      <div>
        <StatusText kind={stored.recordedVersion === null ? "muted" : "success"}>
          {lines.version}
        </StatusText>
      </div>
      <div>
        <StatusText kind="muted">{lines.reachable}</StatusText>
      </div>
    </div>
  );
}

// What the next save would do to the key, which is a different statement from what is stored. Each
// state is a distinct sentence, so nothing here is signalled by colour alone.
function KeyIntent({ draft }: { draft: SettingsDraft }) {
  if (draft.keyCleared) {
    return <StatusText kind="warning">Key will be removed when you save</StatusText>;
  }
  if (draft.apiKey !== "") {
    return <StatusText kind="muted">New key will be saved</StatusText>;
  }
  return null;
}

function TestResult({ test, card }: { test: TransientTest; card: CardGeneration }) {
  if (test.phase === "none") {
    return null;
  }
  if (test.phase === "running") {
    return <StatusText kind="muted">Testing {test.address}</StatusText>;
  }
  if (test.phase === "failed") {
    return <StatusText kind="error">Cove could not run the test: {test.message}</StatusText>;
  }

  const { result } = test;
  const detected = detectionOutcome(result, card);
  if (detected?.kind === "otherGeneration") {
    return (
      <StatusText kind="warning">
        That address answered as {generationLabel(detected.detected)} {detected.version}, not{" "}
        {generationLabel(card)}. Nothing was saved - select {generationLabel(detected.detected)}{" "}
        above to configure it there.
      </StatusText>
    );
  }
  if (result.kind === "connected") {
    // The instance's own version string, unformatted. Reformatting it would report a version no
    // instance runs.
    return (
      <StatusText kind="success">
        Connected to Whisparr {result.version} ({result.generation})
      </StatusText>
    );
  }

  return <StatusText kind="error">{sentenceForKind(result.kind, valuesOf(result))}</StatusText>;
}
