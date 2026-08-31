/**
 * One card per Whisparr generation, each holding that generation's own stored connection.
 *
 * The two cards are never merged into one form. Each generation's connection is remembered
 * separately, so a card always shows the values stored under the generation it names.
 *
 * Two facts are shown separately because they can differ: which generation Cove is set to use, and
 * which card the form below is editing. They diverge from the moment Switch is pressed until the
 * next save.
 *
 * Presentational. Every value arrives as a prop and no request is issued here.
 */
import { SectionCard, StatusPill, StatusText } from "@cove-extensions/ui-shared";

import type { WhisparrSyncSettingsView } from "../wire/api";
import { OptionallyDisabled } from "../common/ui/DisabledControl";
import {
  CARD_GENERATIONS,
  describeRecorded,
  generationLabel,
  valuesForCard,
  type CardGeneration,
} from "./connectLogic";

export interface GenerationCardsProps {
  /** Both generations side by side, or null before the settings read answers. */
  settings: WhisparrSyncSettingsView | null;
  /** The card the form below is editing. */
  card: CardGeneration;
  now: number;
  onShowCard: (card: CardGeneration) => void;
}

export function GenerationCards({ settings, card, now, onShowCard }: GenerationCardsProps) {
  return (
    <SectionCard
      title="Whisparr generation"
      description="Each generation keeps its own connection. Switching shows that generation's stored values."
    >
      <div className="space-y-3">
        {CARD_GENERATIONS.map((generation) => (
          <GenerationCard
            key={generation}
            generation={generation}
            settings={settings}
            showing={generation === card}
            selected={settings?.selectedGeneration === generation}
            now={now}
            onShow={() => {
              onShowCard(generation);
            }}
          />
        ))}
      </div>
    </SectionCard>
  );
}

function GenerationCard({
  generation,
  settings,
  showing,
  selected,
  now,
  onShow,
}: {
  generation: CardGeneration;
  settings: WhisparrSyncSettingsView | null;
  showing: boolean;
  selected: boolean;
  now: number;
  onShow: () => void;
}) {
  const stored = valuesForCard(settings, generation);
  const lines = stored === null ? null : describeRecorded(stored, now);

  return (
    <div className="rounded-xl border border-border bg-card px-3 py-2">
      <div className="flex items-center gap-3">
        <span className="text-sm font-medium text-foreground">{generationLabel(generation)}</span>
        {selected ? <StatusPill variant="green">In use</StatusPill> : null}
        {showing && !selected ? <StatusPill variant="accent">Editing</StatusPill> : null}
        <span className="flex-1" />
        {showing ? null : (
          // No dialog and no save: pressing this discards whatever the form holds.
          <OptionallyDisabled
            name="Switch"
            variant="ghost"
            reason={settings === null ? "Cove is still reading the stored connections." : null}
            onClick={onShow}
          />
        )}
      </div>
      <div className="mt-1 space-y-0.5">
        <div>
          <StatusText kind="muted">
            {stored === null || stored.address === "" ? "No address stored" : stored.address}
          </StatusText>
        </div>
        <div>
          <StatusText kind="muted">
            {stored === null ? "" : stored.keyIsSet ? "Key is set" : "Key not stored"}
          </StatusText>
        </div>
        {/* Only for a card the form is not showing. The section below states the same reading in
            full for the card it is editing, and saying it twice on one screen teaches the reader to
            skip both. */}
        {lines === null || showing ? null : (
          <div>
            <StatusText kind="muted">{lines.version}</StatusText>
          </div>
        )}
      </div>
      {showing && !selected ? (
        <div className="mt-2">
          <StatusText kind="warning">
            Saving below makes this the generation Cove uses, and reloads the page.
          </StatusText>
        </div>
      ) : null}
    </div>
  );
}
