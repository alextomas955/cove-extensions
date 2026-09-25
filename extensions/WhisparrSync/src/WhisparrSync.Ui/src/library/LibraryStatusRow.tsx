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
  NOT_LINKED_REASON,
  STILL_COUNTING,
  WHISPARR_STATUS_ROW,
} from "../common/ui/copy";
import { WhisparrLogo } from "../common/ui/WhisparrLogo";
import { StateGlyph } from "../common/ui/StateGlyph";
import {
  describeState,
  FILE_MARKER,
  NOT_LINKED_MARKER,
  type StateDescription,
  type WhisparrEntityState,
} from "../common/ui/stateVocabularyLogic";
import type { LibraryCardKind } from "../wire/api";
import { useLibraryStatusOn } from "./libraryToggleStore";
import { useLibraryTally } from "./useLibraryTally";

// The states that partition the answered cards, drawn together so the reader can add them up against
// the page's own total. `statusUnknown` is absent because it is drawn only where a card is in it.
const PILL_ORDER: readonly WhisparrEntityState[] = [
  "monitored",
  "unmonitored",
  "notAdded",
  "excluded",
];

// Every figure on the row is one of these, states and markers alike, so the whole row reads as one
// badge family and the key matches the marks on the cards below it.
function CountPill({
  description,
  count,
  title,
}: Readonly<{
  description: StateDescription;
  count: number;
  title?: string;
}>) {
  return (
    <StatusPill
      variant={description.variant}
      shape="tag"
      title={title}
      icon={<StateGlyph iconKey={description.iconKey} />}
    >
      <span className="font-semibold tabular-nums text-foreground">{count}</span>
      <span className="text-secondary">{description.label}</span>
    </StatusPill>
  );
}

function LibraryStatusRow({ kind }: Readonly<{ kind: LibraryCardKind }>) {
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
            <CountPill key={state} description={describeState(state)} count={tally.states[state]} />
          ))}
          {/* Only where a card is in it, so an answered page owes the reader no entry. */}
          {tally.states.statusUnknown === 0 ? null : (
            <CountPill
              description={describeState("statusUnknown")}
              count={tally.states.statusUnknown}
            />
          )}
          {/* Beside the states, not among them: a monitored and an unmonitored card can each hold a
            file, so it partitions nothing.

            Scene cards only. Holding a file is a fact about one scene, and no answer on the studio
            or performer path carries one, so a figure drawn there would read as none held when
            nothing was ever asked. */}
          {kind !== "video" ? null : (
            <CountPill description={FILE_MARKER} count={tally.inLibrary} />
          )}
          {/* The cards nothing was asked about. Without it the figures account for fewer cards than
            the page holds, and a reader cannot tell the difference from a read that went missing.

            Drawn as a pill carrying the same glyph the cards do, because the row is the key to the
            marks below it. Only where a card is in it: it is not a state every page has one of. */}
          {answered - tally.counted === 0 ? null : (
            <CountPill
              description={NOT_LINKED_MARKER}
              count={answered - tally.counted}
              title={NOT_LINKED_REASON}
            />
          )}
        </span>
        {/* A page is answered a batch at a time, so a subtotal is on screen well before the read
            finishes and reads exactly like a finished one. */}
        {answered < registered ? (
          <span className="ml-auto inline-flex items-center gap-1.5 text-xs text-secondary">
            <Spinner />
            {STILL_COUNTING}
          </span>
        ) : null}
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
