/**
 * The two Whisparr generations, side by side inside the Connection section.
 *
 * The option the draft holds is marked and carries no control. The other carries the one control
 * that puts its generation into the draft. Nothing here says a generation is in use: until the save
 * lands, the save bar is what states that the choice is outstanding.
 */
import { FieldGroup, StatusPill, StatusText } from "@cove-extensions/ui-shared";

import type { WhisparrSyncGenerationSettingsView, WhisparrSyncSettingsView } from "../wire/api";
import { OptionallyDisabled } from "../common/ui/DisabledControl";
import { StateGlyph } from "../common/ui/StateGlyph";
import {
  CARD_GENERATIONS,
  generationLabel,
  valuesForCard,
  type CardGeneration,
} from "./connectLogic";

export interface GenerationRowProps {
  settings: WhisparrSyncSettingsView | null;
  /** The generation the draft holds, and so the one a save would make Cove use. */
  drafted: CardGeneration;
  /** The one reason several controls on this page share, stated once by the page's own notice. */
  sharedReason: string | null;
  onChoose: (generation: CardGeneration) => void;
}

export function GenerationRow({
  settings,
  drafted,
  sharedReason,
  onChoose,
}: Readonly<GenerationRowProps>) {
  return (
    <FieldGroup
      label="Whisparr generation"
      labelStyle="mono"
      helper="Each generation keeps its own address and key. Saving moves Cove to the one selected here."
    >
      <div className="grid grid-cols-2 gap-2">
        {CARD_GENERATIONS.map((generation) => (
          <GenerationOption
            key={generation}
            generation={generation}
            stored={valuesForCard(settings, generation)}
            selected={generation === drafted}
            reason={sharedReason}
            onChoose={() => {
              onChoose(generation);
            }}
          />
        ))}
      </div>
    </FieldGroup>
  );
}

// Selected and unselected carry disjoint colour sets. Appending an accent utility to the base would
// lose every colour conflict to the host's later `.bg-card` and `.border-border` rules, and the
// selection would render invisible.
const OPTION_BASE = "rounded-xl border px-3 py-2";
const OPTION_SELECTED = "border-accent bg-accent/15";
const OPTION_UNSELECTED = "border-border bg-card";

function GenerationOption({
  generation,
  stored,
  selected,
  reason,
  onChoose,
}: Readonly<{
  generation: CardGeneration;
  stored: WhisparrSyncGenerationSettingsView | null;
  selected: boolean;
  reason: string | null;
  onChoose: () => void;
}>) {
  const label = generationLabel(generation);
  return (
    <div className={`${OPTION_BASE} ${selected ? OPTION_SELECTED : OPTION_UNSELECTED}`}>
      <div className="flex items-center gap-2">
        <span className="min-w-0 text-sm font-medium text-foreground">{label}</span>
        <span className="flex-1" />
        {selected ? (
          <StatusPill variant="accent" shape="tag" icon={<StateGlyph iconKey="check" />}>
            Selected
          </StatusPill>
        ) : (
          // Pressing this drafts the generation and discards whatever the form holds. There is no
          // dialog: the bar below states what is unsaved, and its discard puts the stored
          // generation back.
          <OptionallyDisabled
            name={`Select ${label}`}
            variant="ghost"
            reason={reason}
            onClick={onChoose}
          />
        )}
      </div>
      <div className="mt-1 space-y-0.5">
        <div>
          <StatusText kind="muted">{addressLine(stored)}</StatusText>
        </div>
        {stored === null ? null : (
          <div>
            <StatusText kind="muted">
              {stored.keyIsSet ? "Key is set" : "Key not stored"}
            </StatusText>
          </div>
        )}
      </div>
    </div>
  );
}

// An option whose stored values have not arrived says so. Reading as an option with nothing stored
// would invite a user to enter what is already there.
function addressLine(stored: WhisparrSyncGenerationSettingsView | null): string {
  if (stored === null) {
    return "Not read yet";
  }
  return stored.address === "" ? "No address stored" : stored.address;
}
