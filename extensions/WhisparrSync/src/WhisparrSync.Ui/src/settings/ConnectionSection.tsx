/**
 * The connection form: an address, a key, and the two recorded lines the instance's own answers
 * wrote.
 *
 * Presentational. Every value arrives as a prop and no request is issued here, so the whole section
 * renders under a test with no host and no network.
 *
 * The key travels IN and never back out. Nothing in {@link ConnectionSectionProps} can carry a stored
 * key, so the pill below reports presence and could not disclose a value even if it tried.
 */
import {
  Field,
  INPUT_CLASS,
  SectionCard,
  Spinner,
  StatusPill,
  StatusText,
  TextInput,
} from "@cove-extensions/ui-shared";

import type { WhisparrSyncGenerationSettingsView } from "../wire/api";
import { AsyncRegion } from "../common/ui/AsyncRegion";
import { OptionallyDisabled } from "../common/ui/DisabledControl";
import { deriveAsyncRegionState } from "../common/ui/asyncRegionLogic";
import {
  describeRecorded,
  detectionOutcome,
  generationLabel,
  recordedRead,
  sentenceForKind,
  valuesOf,
  type CardGeneration,
  type GenerationDraft,
  type TransientTest,
} from "./connectLogic";
import type { SaveState } from "./connectionStore";

/** The placeholder the panel's own end-to-end specs locate the address field by. */
const ADDRESS_PLACEHOLDER = "http://whisparr:6969";

export interface ConnectionSectionProps {
  /** The generation this form is editing. */
  card: CardGeneration;
  /** The generation's stored values, or null before the settings read answers. */
  stored: WhisparrSyncGenerationSettingsView | null;
  /** Whether the settings read failed. */
  readFailed: boolean;
  draft: GenerationDraft;
  test: TransientTest;
  save: SaveState;
  /** Whether saving would write nothing that is not already stored. */
  noOpSave: boolean;
  /** Whether Test asks about the stored connection rather than about the pair in the form. */
  testsStored: boolean;
  /**
   * The one reason several controls on this page share, stated once by the page's own notice. It
   * still reaches each control's accessible name, which is per-control by nature.
   */
  sharedReason: string | null;
  /** The instant the relative times are measured against. */
  now: number;
  onAddressChange: (next: string) => void;
  onKeyChange: (next: string) => void;
  onClearStoredKey: (cleared: boolean) => void;
  onTest: () => void;
  onSave: () => void;
}

export function ConnectionSection({
  card,
  stored,
  readFailed,
  draft,
  test,
  save,
  noOpSave,
  testsStored,
  sharedReason,
  now,
  onAddressChange,
  onKeyChange,
  onClearStoredKey,
  onTest,
  onSave,
}: ConnectionSectionProps) {
  const testing = test.phase === "running";
  const saving = save.status === "saving";

  // A key already stored cannot be sent back, so testing an address the form has changed needs one
  // typed. Testing the address as stored does not: that test asks about the stored connection.
  const testReason =
    sharedReason ??
    (testing
      ? "This test is still running."
      : draft.address.trim() === ""
        ? "Enter the Whisparr address first."
        : !testsStored && draft.apiKey === ""
          ? "Enter the Whisparr API key to test this address."
          : null);

  const saveReason =
    sharedReason ??
    (saving ? "This save is still running." : noOpSave ? "Nothing has changed." : null);

  return (
    <SectionCard title="Connection" description="The Whisparr instance Cove keeps in step with.">
      <div className="space-y-4">
        <AsyncRegion
          state={deriveAsyncRegionState(recordedRead(stored, readFailed))}
          reading={<StatusText kind="muted">Reading the stored connection…</StatusText>}
          content={<RecordedLines stored={stored} now={now} />}
          empty={<RecordedLines stored={stored} now={now} />}
          failed={
            <StatusText kind="error">
              Cove could not read what is stored for this connection.
            </StatusText>
          }
        />

        <Field
          label="Whisparr address"
          helper="The address Cove itself reaches Whisparr on, including the scheme and port."
        >
          <TextInput
            value={draft.address}
            onChange={onAddressChange}
            placeholder={ADDRESS_PLACEHOLDER}
          />
        </Field>

        <Field
          label="API key"
          helper="Leave blank to keep the key already stored for this generation."
        >
          <input
            type="password"
            value={draft.apiKey}
            onChange={(e) => {
              onKeyChange(e.target.value);
            }}
            className={INPUT_CLASS}
            autoComplete="off"
          />
        </Field>

        <div className="flex items-center gap-3">
          <KeyState stored={stored} draft={draft} />
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

        <div className="flex items-center gap-3" aria-busy={saving}>
          <OptionallyDisabled
            name={saving ? "Saving…" : "Save connection"}
            variant="ghost"
            reason={saveReason}
            onClick={onSave}
          />
          {saving ? <Spinner /> : null}
          <SaveResult save={save} />
        </div>
      </div>
    </SectionCard>
  );
}

/**
 * The two lines §07 keeps apart, because they measure different things: a version verified last week
 * beside an instance reached a minute ago is honest rather than contradictory.
 */
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

/**
 * Whether a key is stored, and what the next save would do to it. Never any part of the value.
 *
 * The four labels are distinct sentences rather than one label in four tints, so nothing here is
 * signalled by colour alone. The glyphs §16 fixes to the entity states are deliberately not reused:
 * one vocabulary means one meaning per glyph.
 */
function KeyState({
  stored,
  draft,
}: {
  stored: WhisparrSyncGenerationSettingsView | null;
  draft: GenerationDraft;
}) {
  if (draft.keyCleared) {
    return <StatusPill variant="amber">Key will be removed when you save</StatusPill>;
  }
  if (draft.apiKey !== "") {
    return <StatusPill variant="accent">New key will be saved</StatusPill>;
  }
  if (stored === null) {
    return null;
  }
  return stored.keyIsSet ? (
    <StatusPill variant="green">Key is set</StatusPill>
  ) : (
    <StatusPill variant="gray">Key not stored</StatusPill>
  );
}

/**
 * The one transient result line, always about the address that was in the field when it ran.
 *
 * A success that reached the OTHER generation names the version found and stops. Each generation's
 * connection is remembered separately, so nothing is written and the card is switched deliberately.
 */
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
        {generationLabel(card)}. Nothing was saved - switch to the{" "}
        {generationLabel(detected.detected)} card to configure it there.
      </StatusText>
    );
  }
  if (result.kind === "connected") {
    // The instance's own version string, character for character. A rendering that reformatted it
    // would report a version no instance runs.
    return (
      <StatusText kind="success">
        Connected to Whisparr {result.version} ({result.generation})
      </StatusText>
    );
  }

  return <StatusText kind="error">{sentenceForKind(result.kind, valuesOf(result))}</StatusText>;
}

/** HON-9's third bullet: a settings save confirms inline, on the section that changed. */
function SaveResult({ save }: { save: SaveState }) {
  if (save.status === "saved") {
    return <StatusText kind="success">Connection saved.</StatusText>;
  }
  if (save.status === "failed") {
    return <StatusText kind="error">Cove could not save: {save.message}</StatusText>;
  }
  return null;
}
