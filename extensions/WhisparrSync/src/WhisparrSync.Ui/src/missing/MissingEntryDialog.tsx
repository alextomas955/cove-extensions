/**
 * The panel the catalogue tab's add control opens: one row per way the connected instance can be
 * asked to add the entity, and a way out that sends nothing.
 *
 * Nothing leaves the browser from here. The chosen mode is handed back, and the row that marks the
 * back catalogue is handed back only once the confirmation in front of it has been accepted.
 */
import { useState } from "react";
import { createPortal } from "react-dom";

import {
  ADD_TO_WHISPARR,
  ADD_TO_WHISPARR_TRACKS_ONLY,
  BULK_CANCEL,
  BULK_CLOSE,
  allScenesConfirmation,
} from "../common/ui/copy";
import { ChoiceOverlay, type RowIcon } from "../common/ui/ChoiceOverlay";
import { VERB_GLYPH } from "../common/ui/verbGlyphs";
import type { MissingEntryMode } from "../wire/api";
import { ConfirmDialog } from "./hostComponents";
import type { MissingEntryChoice, MissingEntryRow } from "./missingEntryLogic";

// Every row here covers the one entity whose page the tab is open on.
const ONE_ENTITY = 1;

// The verb each row carries, so a mode keeps the shape it has in the entity menu.
const ROW_ICON: Record<MissingEntryMode, RowIcon> = {
  trackOnly: VERB_GLYPH.add,
  monitorNewReleases: VERB_GLYPH.monitorNewReleases,
  monitorAllScenes: VERB_GLYPH.monitorBackCatalogue,
};

// What a row does that its name does not say. The two monitor rows carry Whisparr's own scope
// names, which say it; what the catalogue add leaves unmonitored is only said here.
const ROW_DETAIL: Record<MissingEntryMode, string | null> = {
  trackOnly: ADD_TO_WHISPARR_TRACKS_ONLY,
  monitorNewReleases: null,
  monitorAllScenes: null,
};

type EntryChoiceRow = MissingEntryRow & {
  readonly key: string;
  readonly icon: RowIcon;
  readonly detail: string | null;
};

export function MissingEntryDialog({
  choice,
  onChoose,
}: Readonly<{
  choice: MissingEntryChoice;
  /** Called with the chosen mode, or with null when the reader leaves without choosing. */
  onChoose: (mode: MissingEntryMode | null) => void;
}>) {
  const [confirming, setConfirming] = useState<MissingEntryRow | null>(null);

  const rows: EntryChoiceRow[] = choice.rows.map((row) => ({
    ...row,
    key: row.mode,
    icon: ROW_ICON[row.mode],
    detail: ROW_DETAIL[row.mode],
  }));

  // Portaled for the reason the entity menu's own panel is: a host hero clips its children, and a
  // panel placed against the viewport would be cut to that rectangle rather than cover the page.
  return createPortal(
    confirming === null ? (
      <ChoiceOverlay<EntryChoiceRow>
        header={ADD_TO_WHISPARR}
        reason={choice.reason}
        rows={rows}
        cancelLabel={BULK_CANCEL}
        closeLabel={BULK_CLOSE}
        onChoose={(row) => {
          if (row === null) {
            onChoose(null);
            return;
          }

          if (row.marksTheBackCatalogue) {
            setConfirming(row);
            return;
          }

          onChoose(row.mode);
        }}
      />
    ) : (
      <ConfirmDialog
        open
        // Red where the choice cannot be undone and accent where it can, rather than the host's
        // default of always red.
        destructive={choice.oneWayDoor}
        title={confirming.label}
        confirmLabel={confirming.label}
        message={allScenesConfirmation(ONE_ENTITY, choice.oneWayDoor)}
        onConfirm={() => {
          onChoose(confirming.mode);
        }}
        onCancel={() => {
          setConfirming(null);
        }}
      />
    ),
    document.body,
  );
}
