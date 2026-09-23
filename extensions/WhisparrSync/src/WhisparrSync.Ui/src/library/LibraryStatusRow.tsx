/**
 * The full-width row under a list toolbar: how many cards on the page are in each state.
 *
 * It asks for nothing. Every figure comes from the answers the badges already hold, so the row
 * cannot report a page the badges are not drawing. It states no reason of its own; why a page could
 * not be answered for is the toolbar control's one sentence.
 */
import type { ReactNode } from "react";
import { Spinner, StatusPill } from "@cove-extensions/ui-shared";

import {
  CHECKING_WHISPARR,
  LIBRARY_COUNTS_ARE_FOR_THIS_PAGE,
  NOT_ADDED_ON_THIS_PAGE,
  NOT_LINKED_ON_THIS_PAGE,
  NOT_LINKED_REASON,
  STILL_COUNTING,
  WHISPARR_STATUS_ROW,
} from "../common/ui/copy";
import { WhisparrLogo } from "../common/ui/WhisparrLogo";
import { StateGlyph } from "../common/ui/StateGlyph";
import {
  describeState,
  FILE_MARKER,
  type WhisparrEntityState,
} from "../common/ui/stateVocabularyLogic";
import type { LibraryCardKind } from "../wire/api";
import { useLibraryStatusOn } from "./libraryToggleStore";
import { useLibraryTally } from "./useLibraryTally";

// `notAdded` is absent because it is the trailing count on the right. `statusUnknown` is absent
// because it is drawn only where a card is in it.
const PILL_ORDER: readonly WhisparrEntityState[] = ["monitored", "unmonitored", "excluded"];

// Drawn at a count of zero too: the row is the key to the glyphs on the cards below it, and a key
// that drops its empty entries changes as you page.
function StatePill({ state, count }: { state: WhisparrEntityState; count: number }) {
  const description = describeState(state);
  return (
    <StatusPill
      variant={description.variant}
      shape="tag"
      icon={<StateGlyph iconKey={description.iconKey} />}
    >
      <span className="font-semibold tabular-nums text-foreground">{count}</span>
      <span className="text-secondary">{description.label}</span>
    </StatusPill>
  );
}

function LibraryStatusRow({ kind }: { kind: LibraryCardKind }) {
  const on = useLibraryStatusOn();
  const { tally, registered, answered } = useLibraryTally(kind, on);

  // Nothing with the control off, on a display mode that mounts no card, or on a page the extension
  // cannot speak for. In the last case the toolbar control carries the reason, and a row of zeroes
  // beside it would contradict it.
  if (!on || registered === 0 || (answered > 0 && tally.counted === 0)) {
    return null;
  }

  let body: ReactNode;
  if (answered === 0) {
    body = (
      <span className="inline-flex items-center gap-2 text-xs text-secondary">
        <Spinner />
        {CHECKING_WHISPARR}
      </span>
    );
  } else {
    // A figure still climbing is held back from full strength, and settles into it once the read is
    // done. The fade is the design language's own: a short one, no loop, nothing that draws the eye
    // away from the page. It never carries the meaning by itself - the spinner and its sentence do
    // that - so a reader who cannot see the difference loses nothing.
    const counting = answered < registered;

    body = (
      <span className="flex flex-1 flex-wrap items-center gap-2">
        <span
          className={`flex flex-wrap items-center gap-2 transition-opacity ${counting ? "opacity-60" : ""}`}
        >
          {PILL_ORDER.map((state) => (
            <StatePill key={state} state={state} count={tally.states[state]} />
          ))}
          {/* Only where a card is in it, so an answered page owes the reader no entry. */}
          {tally.states.statusUnknown === 0 ? null : (
            <StatePill state="statusUnknown" count={tally.states.statusUnknown} />
          )}
          {/* Beside the states, not among them: a monitored and an unmonitored card can each hold a
            file, so it partitions nothing.

            Scene cards only. Holding a file is a fact about one scene, and no answer on the studio
            or performer path carries one, so a figure drawn there would read as none held when
            nothing was ever asked. */}
          {kind !== "video" ? null : (
            <StatusPill
              variant={FILE_MARKER.variant}
              shape="tag"
              icon={<StateGlyph iconKey={FILE_MARKER.iconKey} />}
            >
              <span className="font-semibold tabular-nums text-foreground">{tally.inLibrary}</span>
              <span className="text-secondary">{FILE_MARKER.label}</span>
            </StatusPill>
          )}
          {/* The cards nothing was asked about. Without it the figures account for fewer cards than
            the page holds, and a reader cannot tell the difference from a read that went missing.

            Drawn as a pill carrying the same glyph the cards do, because the row is the key to the
            marks below it. Only where a card is in it: it is not a state every page has one of. */}
          {answered - tally.counted === 0 ? null : (
            <StatusPill
              variant="gray"
              shape="tag"
              icon={<StateGlyph iconKey="unlink" />}
              title={NOT_LINKED_REASON}
            >
              <span className="font-semibold tabular-nums text-foreground">
                {answered - tally.counted}
              </span>
              <span className="text-secondary">{NOT_LINKED_ON_THIS_PAGE}</span>
            </StatusPill>
          )}
        </span>
        <span className="ml-auto inline-flex items-center gap-2 text-xs text-muted">
          {/* A page is answered a batch at a time, so a subtotal is on screen well before the
              read finishes and reads exactly like a finished one. */}
          {answered < registered ? (
            <span className="inline-flex items-center gap-1.5 text-secondary">
              <Spinner />
              {STILL_COUNTING}
            </span>
          ) : null}
          <span
            className={`inline-flex items-center gap-1 transition-opacity ${counting ? "opacity-60" : ""}`}
          >
            <span className="font-semibold tabular-nums">{tally.states.notAdded}</span>
            {NOT_ADDED_ON_THIS_PAGE}
          </span>
        </span>
      </span>
    );
  }

  return (
    <div
      role="status"
      aria-live="polite"
      title={LIBRARY_COUNTS_ARE_FOR_THIS_PAGE}
      className="mx-1 mt-1 flex flex-wrap items-center gap-3 rounded-lg border border-border bg-card/80 px-3 py-2"
    >
      <span className="inline-flex items-center gap-1.5 text-xs font-semibold uppercase tracking-wide text-accent">
        <WhisparrLogo className="h-4 w-4" />
        {WHISPARR_STATUS_ROW}
      </span>
      {body}
    </div>
  );
}

export function WhisparrVideoLibraryRow() {
  return <LibraryStatusRow kind="video" />;
}

export function WhisparrStudioLibraryRow() {
  return <LibraryStatusRow kind="studio" />;
}

export function WhisparrPerformerLibraryRow() {
  return <LibraryStatusRow kind="performer" />;
}
