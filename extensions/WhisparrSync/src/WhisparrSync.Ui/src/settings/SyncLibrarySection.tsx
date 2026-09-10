/**
 * How much of the library Whisparr already knows about, and the control that finds out.
 *
 * Presentational. Every value arrives as a prop and no request is issued here.
 *
 * No progress readout of any kind: Cove's job list is the progress surface, and a second one here
 * would be the one that goes stale. Nothing from a job-status read reaches this component.
 */
import { SectionCard, Spinner, StatusText } from "@cove-extensions/ui-shared";

import type { SyncPreviewView } from "../wire/api";
import { AsyncRegion } from "../common/ui/AsyncRegion";
import { OptionallyDisabled } from "../common/ui/DisabledControl";
import {
  SYNC_ALREADY_IN_WHISPARR,
  SYNC_COUNTING,
  SYNC_COUNT_DID_NOT_FINISH,
  SYNC_NOTHING_COUNTED_YET,
  SYNC_NOT_YET_IN_WHISPARR,
  SYNC_SKIPPED_CANNOT_BE_REGISTERED,
  SYNC_SKIPPED_NO_ID,
} from "../common/ui/copy";
import type { AsyncRegionState } from "../common/ui/asyncRegionLogic";
import { describeInstant } from "./relativeTimeLogic";
import { countControl, groupThousands } from "./syncLibraryLogic";

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
}

export function SyncLibrarySection({
  counts,
  preview,
  counting,
  now,
  onCount,
}: SyncLibrarySectionProps) {
  const control = countControl(counting, counts !== null);

  return (
    <SectionCard
      title="Sync your library to Whisparr"
      description="Register the scenes you already own, so Whisparr knows about them."
    >
      <div className="space-y-2" aria-busy={counting}>
        <AsyncRegion
          state={preview}
          reading={
            <div className="flex items-center gap-3">
              <Spinner />
              <StatusText kind="muted">{SYNC_COUNTING}</StatusText>
            </div>
          }
          content={counts === null ? null : <Counts counts={counts} now={now} />}
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
function Counts({ counts, now }: { counts: SyncPreviewView; now: number }) {
  const age = describeInstant(counts.countedAt, now);

  return (
    <div className="space-y-2">
      <CountRow label={SYNC_NOT_YET_IN_WHISPARR} value={counts.notYetThere} />
      <CountRow label={SYNC_ALREADY_IN_WHISPARR} value={counts.alreadyThere} />
      <CountRow label={SYNC_SKIPPED_NO_ID} value={counts.skipped} />

      <StatusText kind="muted">
        {age === null
          ? SYNC_SKIPPED_CANNOT_BE_REGISTERED
          : `Counted ${age.text}. ${SYNC_SKIPPED_CANNOT_BE_REGISTERED}`}
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
