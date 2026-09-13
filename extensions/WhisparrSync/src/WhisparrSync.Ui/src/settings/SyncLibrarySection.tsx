/**
 * How much of the library Whisparr already knows about, and the control that finds out.
 *
 * Presentational. Every value arrives as a prop and no request is issued here.
 *
 * No progress readout of any kind: Cove's job list is the progress surface, and a second one here
 * would be the one that goes stale. Nothing from a job-status read reaches this component.
 */
import { useState } from "react";
import { createPortal } from "react-dom";
import { SectionCard, Spinner, StatusText } from "@cove-extensions/ui-shared";

import type { SyncPreviewView } from "../wire/api";
import { AsyncRegion } from "../common/ui/AsyncRegion";
import { OptionallyDisabled } from "../common/ui/DisabledControl";
import { DisabledToggle } from "../common/ui/DisabledToggle";
import {
  MONITOR_ALL_DOWNLOADS_NOTHING_BY_ITSELF,
  RUN_WAS_NOT_STARTED,
  SYNC_ALREADY_IN_WHISPARR,
  SYNC_ALSO_MONITOR,
  SYNC_COUNTING,
  SYNC_COUNT_DID_NOT_FINISH,
  SYNC_LIBRARY,
  SYNC_NOTHING_COUNTED_YET,
  SYNC_NOT_YET_IN_WHISPARR,
  SYNC_RUNS_IN_THE_JOB_DRAWER,
  SYNC_SKIPPED_CANNOT_BE_IDENTIFIED,
} from "../common/ui/copy";
import type { AsyncRegionState } from "../common/ui/asyncRegionLogic";
import { ConfirmDialog } from "./hostComponents";
import { describeInstant } from "./relativeTimeLogic";
import {
  countControl,
  groupThousands,
  monitorToggleReason,
  syncConfirmation,
  syncDisabledReason,
  type SyncControlState,
  type SyncSentences,
} from "./syncLibraryLogic";

export interface SyncLibrarySectionProps {
  /** The counts held, or null when none are. */
  counts: SyncPreviewView | null;
  /** Which of the four slots the preview region renders. */
  preview: AsyncRegionState;
  /** Whether a count is queued or running. */
  counting: boolean;
  /** The instant the page re-reads on an interval, which the counts' age is measured against. */
  now: number;
  onCount: () => void;
  /** The page's own reason nothing here can act on the stored connection, or null. */
  sharedReason: string | null;
  /** Whether the read answered that no instance is connected. */
  noConnection: boolean;
  /** Whether a library sync is already in flight. */
  syncRunning: boolean;
  /** Whether the enqueue request itself is in flight. */
  starting: boolean;
  /** Whether a run was started, which is the whole of what this section says afterwards. */
  started: boolean;
  /** Whether the enqueue was refused, in which case nothing was changed. */
  refused: boolean;
  /** The monitor choice as it stands. */
  monitorAlso: boolean;
  /** The sentences to state, resolved by the caller from what the read says the run registers. */
  sentences: SyncSentences;
  onMonitorAlso: (checked: boolean) => void;
  onSync: () => void;
}

export function SyncLibrarySection({
  counts,
  preview,
  counting,
  now,
  onCount,
  sharedReason,
  noConnection,
  syncRunning,
  starting,
  started,
  refused,
  monitorAlso,
  sentences,
  onMonitorAlso,
  onSync,
}: SyncLibrarySectionProps) {
  const control = countControl(counting, counts !== null);
  const [confirming, setConfirming] = useState(false);

  const state: SyncControlState = {
    sharedReason,
    noConnection,
    syncRunning,
    starting,
    counts,
    monitorAlso,
    sentences,
  };
  const syncReason = syncDisabledReason(state);

  return (
    <SectionCard title="Sync your library to Whisparr" description={sentences.description}>
      <div className="space-y-2" aria-busy={counting}>
        <AsyncRegion
          state={preview}
          reading={
            <div className="flex items-center gap-3">
              <Spinner />
              <StatusText kind="muted">{SYNC_COUNTING}</StatusText>
            </div>
          }
          content={
            counts === null ? null : (
              <Counts counts={counts} now={now} remedy={sentences.skippedRemedy} />
            )
          }
          empty={<StatusText kind="muted">{SYNC_NOTHING_COUNTED_YET}</StatusText>}
          failed={<StatusText kind="error">{SYNC_COUNT_DID_NOT_FINISH}</StatusText>}
        />
      </div>

      <div className="flex items-center gap-3">
        <OptionallyDisabled
          name={control.name}
          variant="ghost"
          reason={control.reason}
          onClick={onCount}
        />
        {counting ? <Spinner /> : null}
      </div>

      <DisabledToggle
        label={SYNC_ALSO_MONITOR}
        helper={MONITOR_ALL_DOWNLOADS_NOTHING_BY_ITSELF}
        checked={monitorAlso}
        onChange={onMonitorAlso}
        reason={monitorToggleReason(state)}
      />

      <div className="space-y-2">
        <div className="flex items-center gap-3">
          <OptionallyDisabled
            name={SYNC_LIBRARY}
            variant="primary"
            reason={syncReason}
            onClick={() => {
              setConfirming(true);
            }}
          />
        </div>

        {refused ? <StatusText kind="error">{RUN_WAS_NOT_STARTED}</StatusText> : null}
        {started && !refused ? (
          <StatusText kind="muted">{SYNC_RUNS_IN_THE_JOB_DRAWER}</StatusText>
        ) : null}
      </div>

      {!confirming || counts === null
        ? null
        : // Portaled to the document, so no ancestor of this tab can become the containing block of a
          // dialog that positions against the viewport and clip it to the settings column.
          createPortal(
            <ConfirmDialog
              open
              // The host defaults this to true and paints the confirm button red. Registering
              // scenes creates nothing a reader loses and triggers no download, so a red button
              // would contradict the sentence this dialog exists to state.
              destructive={false}
              title={SYNC_LIBRARY}
              confirmLabel={SYNC_LIBRARY}
              message={syncConfirmation(counts, monitorAlso, sentences)}
              onConfirm={() => {
                setConfirming(false);
                onSync();
              }}
              onCancel={() => {
                setConfirming(false);
              }}
            />,
            document.body,
          )}
    </SectionCard>
  );
}

/**
 * The three counts, their age, and what the skipped one means.
 *
 * The three arrive on one value, so a partial set is unrepresentable here. Each row puts its noun in
 * the label and its number in the value, so no row has a plural to disagree with and a zero still
 * renders its own label.
 */
function Counts({ counts, now, remedy }: { counts: SyncPreviewView; now: number; remedy: string }) {
  const age = describeInstant(counts.countedAt, now);

  return (
    <div className="space-y-2">
      <CountRow label={SYNC_NOT_YET_IN_WHISPARR} value={counts.notYetThere} />
      <CountRow label={SYNC_ALREADY_IN_WHISPARR} value={counts.alreadyThere} />
      <CountRow label={SYNC_SKIPPED_CANNOT_BE_IDENTIFIED} value={counts.skipped} />

      <StatusText kind="muted">
        {age === null ? remedy : `Counted ${age.text}. ${remedy}`}
      </StatusText>
    </div>
  );
}

function CountRow({ label, value }: { label: string; value: number }) {
  return (
    <div className="flex items-baseline justify-between gap-2">
      <span className="min-w-0 text-sm text-secondary">{label}</span>
      <span className="shrink-0 text-sm font-semibold text-foreground">
        {groupThousands(value)}
      </span>
    </div>
  );
}
