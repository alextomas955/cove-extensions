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
import {
  CONNECT_ADDRESS,
  CONNECT_ADDRESS_HELPER,
  CONNECT_ADDRESS_PLACEHOLDER,
  CONNECT_API_KEY,
  CONNECT_API_KEY_HELPER,
  CONNECT_CLEAR_STORED_KEY,
  CONNECT_DESCRIPTION,
  CONNECT_KEEP_STORED_KEY,
  CONNECT_READING_THE_STORED_CONNECTION,
  CONNECT_STORED_CONNECTION_NOT_READ,
  CONNECT_TEST,
  CONNECT_TESTING,
  CONNECT_TITLE,
  connectedSentence,
  connectOtherGenerationSentence,
  connectTestDidNotRunSentence,
  connectTestingSentence,
  KEY_WILL_BE_REMOVED_ON_SAVE,
  NEW_KEY_WILL_BE_SAVED,
  READ_IS_STALE,
} from "../common/ui/copy";
import { GenerationRow } from "./GenerationRow";
import { KeyStateField } from "./KeyStatePill";
import {
  describeRecorded,
  detectionOutcome,
  generationLabel,
  recordedRead,
  sentenceForKind,
  testUnavailability,
  valuesForCard,
  valuesOf,
  type CardGeneration,
  type TransientTest,
} from "./connectLogic";
import type { SettingsDraft } from "./settingsDraftLogic";

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
}: Readonly<ConnectionSectionProps>) {
  const testing = test.phase === "running";
  const stored = valuesForCard(settings, card);

  const testReason = testUnavailability({
    shared: sharedReason,
    running: testing,
    address: draft.address,
    apiKey: draft.apiKey,
    testsStored,
  });

  return (
    <SectionCard title={CONNECT_TITLE} description={CONNECT_DESCRIPTION}>
      <div className="space-y-4">
        <GenerationRow
          settings={settings}
          drafted={card}
          sharedReason={sharedReason}
          onChoose={onChooseGeneration}
        />

        <AsyncRegion
          state={deriveAsyncRegionState(recordedRead(stored, readFailed))}
          reading={<StatusText kind="muted">{CONNECT_READING_THE_STORED_CONNECTION}</StatusText>}
          content={<RecordedLines stored={stored} now={now} />}
          empty={<RecordedLines stored={stored} now={now} />}
          outageNotice={<StatusText kind="error">{READ_IS_STALE}</StatusText>}
          failed={<StatusText kind="error">{CONNECT_STORED_CONNECTION_NOT_READ}</StatusText>}
        />

        <Field label={CONNECT_ADDRESS} labelStyle="mono" helper={CONNECT_ADDRESS_HELPER}>
          {(id) => (
            <TextInput
              id={id}
              value={draft.address}
              onChange={onAddressChange}
              placeholder={CONNECT_ADDRESS_PLACEHOLDER}
            />
          )}
        </Field>

        <Field label={CONNECT_API_KEY} labelStyle="mono" helper={CONNECT_API_KEY_HELPER}>
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
              name={CONNECT_CLEAR_STORED_KEY}
              variant="ghost"
              reason={sharedReason}
              onClick={() => {
                onClearStoredKey(true);
              }}
            />
          ) : null}
          {draft.keyCleared ? (
            <OptionallyDisabled
              name={CONNECT_KEEP_STORED_KEY}
              variant="ghost"
              reason={sharedReason}
              onClick={() => {
                onClearStoredKey(false);
              }}
            />
          ) : null}
        </div>

        {/* Checking the connection is a different subject from setting it, so a hairline closes
            the fields above rather than spacing alone. */}
        <div className="flex items-center gap-3 border-t border-border pt-4" aria-busy={testing}>
          <OptionallyDisabled
            name={testing ? CONNECT_TESTING : CONNECT_TEST}
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
}: Readonly<{
  stored: WhisparrSyncGenerationSettingsView | null;
  now: number;
}>) {
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
function KeyIntent({ draft }: Readonly<{ draft: SettingsDraft }>) {
  if (draft.keyCleared) {
    return <StatusText kind="warning">{KEY_WILL_BE_REMOVED_ON_SAVE}</StatusText>;
  }
  if (draft.apiKey !== "") {
    return <StatusText kind="muted">{NEW_KEY_WILL_BE_SAVED}</StatusText>;
  }
  return null;
}

function TestResult({ test, card }: Readonly<{ test: TransientTest; card: CardGeneration }>) {
  if (test.phase === "none") {
    return null;
  }
  if (test.phase === "running") {
    return <StatusText kind="muted">{connectTestingSentence(test.address)}</StatusText>;
  }
  if (test.phase === "failed") {
    return <StatusText kind="error">{connectTestDidNotRunSentence(test.message)}</StatusText>;
  }

  const { result } = test;
  const detected = detectionOutcome(result, card);
  if (detected?.kind === "otherGeneration") {
    return (
      <StatusText kind="warning">
        {connectOtherGenerationSentence(
          generationLabel(detected.detected),
          detected.version,
          generationLabel(card),
        )}
      </StatusText>
    );
  }
  if (result.kind === "connected") {
    return (
      <StatusText kind="success">{connectedSentence(result.version, result.generation)}</StatusText>
    );
  }

  return <StatusText kind="error">{sentenceForKind(result.kind, valuesOf(result))}</StatusText>;
}
