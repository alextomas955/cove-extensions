/**
 * One catalogue scene the library does not hold.
 *
 * Hand-built on the host's own utility classes rather than through a host card component. The host's
 * video card takes a Cove entity and navigates to that entity's page, and a provider scene has
 * neither an id nor a page, so the card carries no card-level link at all. That is also what keeps
 * it to three focus stops.
 *
 * The cover is fetched by the browser straight from the provider's own address: nothing is proxied
 * and nothing is stored. Every provider-supplied string renders as an escaped text node.
 */
import { Check, ImageOff, User } from "lucide-react";

import { StateChip } from "../common/ui/StateChip";
import type { MissingCard as MissingCardView, MissingPerformerChip } from "../wire/api";
import {
  deriveCardRows,
  overflowChipCount,
  visiblePerformerChips,
  type CardRows,
} from "./missingCardLogic";
// A type-only import, so it is erased at build and adds no runtime import of the host barrel. The
// one-module rule the rest of this surface follows exists to bound what a wrong export name can take
// down at bundle load, and an erased type cannot reach load at all.
import type { MultiSelectToggleOptions } from "@cove/runtime/components";
import {
  CARD_BODY_CLASS,
  CARD_CLASS,
  CARD_MEDIA_CLASS,
  CARD_META_CLASS,
  CARD_SELECTED_CLASS,
  CARD_TITLE_CLASS,
} from "./missingClasses";

/**
 * The name this tab gives a monitored scene.
 *
 * The underlying state is unchanged and so are its glyph and tint; only the word differs, because a
 * scene the library does not hold is one the reader is waiting for rather than one being watched.
 */
const MONITORED_LABEL = "Wanted";

/** What the selection control is called, in each of the two states it can be pressed from. */
const SELECT_LABEL = "Select scene";
const DESELECT_LABEL = "Deselect scene";

/**
 * The performer chip's own name width.
 *
 * Inline because the host's Tailwind JIT never scans this bundle, so an arbitrary-value width class
 * would contribute no declaration at all.
 */
const CHIP_NAME_WIDTH = { maxWidth: "80px" };

export function MissingCard({
  card,
  selected = false,
  selecting = false,
  onToggleSelect,
}: {
  card: MissingCardView;
  selected?: boolean;
  /** A selection is in progress, so every card shows its control rather than only the hovered one. */
  selecting?: boolean;
  /** Absent where the surface offers no selection, which draws no control rather than an inert one. */
  onToggleSelect?: (options?: MultiSelectToggleOptions) => void;
}) {
  const rows = deriveCardRows(card);

  return (
    <article className={selected ? CARD_SELECTED_CLASS : CARD_CLASS}>
      <div className={CARD_MEDIA_CLASS}>
        <Cover coverUrl={card.coverUrl} title={card.title} />
        {onToggleSelect === undefined ? null : (
          <SelectionToggle
            selected={selected}
            selecting={selecting}
            onToggleSelect={onToggleSelect}
          />
        )}
      </div>
      <div className={CARD_BODY_CLASS}>
        <h3 className={CARD_TITLE_CLASS} title={card.title}>
          {card.title}
        </h3>
        <CardBodyRows rows={rows} />
        <div className="mt-1">
          <StateChip
            state={card.state}
            label={card.state === "monitored" ? MONITORED_LABEL : undefined}
          />
        </div>
      </div>
    </article>
  );
}

function CardBodyRows({ rows }: { rows: CardRows }) {
  return (
    <>
      {rows.meta === null ? null : (
        <div className={CARD_META_CLASS}>
          {rows.meta.releaseDate === null ? null : <span>{rows.meta.releaseDate}</span>}
          {rows.meta.studioName === null ? null : (
            <span className="truncate">{rows.meta.studioName}</span>
          )}
        </div>
      )}
      {rows.performers === null ? null : <PerformerChips performers={rows.performers} />}
      {rows.description === null ? null : (
        <p className="text-xs text-secondary line-clamp-2 leading-snug">{rows.description}</p>
      )}
      {rows.counts === null ? null : <p className="text-xs text-muted">{rows.counts}</p>}
    </>
  );
}

function Cover({ coverUrl, title }: { coverUrl: string | null; title: string }) {
  if (coverUrl === null) {
    return (
      <div
        role="img"
        aria-label={title}
        className="flex h-full w-full items-center justify-center bg-card"
      >
        <ImageOff className="h-8 w-8 text-muted" aria-hidden="true" />
      </div>
    );
  }

  return <img src={coverUrl} alt={title} loading="lazy" className="h-full w-full object-cover" />;
}

// The picture is the provider's and only the provider's: a provider performer carries no Cove id, so
// there is nothing to look one up by and no page for a chip to lead to. The no-picture fallback is
// the glyph Cove's own performer badge falls back to.
function PerformerChips({ performers }: { performers: readonly MissingPerformerChip[] }) {
  const overflow = overflowChipCount(performers);

  return (
    <div className="flex flex-wrap items-center gap-1">
      {visiblePerformerChips(performers).map((performer) => (
        <span
          key={performer.providerPerformerId}
          className="performer-badge flex min-w-0 items-center gap-1 rounded-full border border-border bg-surface px-1.5 py-0.5"
        >
          {performer.imageUrl === null ? (
            <User className="h-3.5 w-3.5 flex-shrink-0 text-muted" aria-hidden="true" />
          ) : (
            <img
              src={performer.imageUrl}
              alt=""
              loading="lazy"
              className="h-4 w-4 flex-shrink-0 rounded-full object-cover"
            />
          )}
          <span className="truncate text-xs text-secondary" style={CHIP_NAME_WIDTH}>
            {performer.name}
          </span>
        </span>
      ))}
      {overflow === 0 ? null : <span className="text-xs text-muted">+{overflow}</span>}
    </div>
  );
}

// Cove's own control is revealed on hover alone, which is invisible to someone who never hovers, so
// the focus state reveals it too.
function SelectionToggle({
  selected,
  selecting,
  onToggleSelect,
}: {
  selected: boolean;
  selecting: boolean;
  onToggleSelect: (options?: MultiSelectToggleOptions) => void;
}) {
  const revealed = selected || selecting ? "opacity-100" : "opacity-0 group-hover:opacity-100";

  return (
    <button
      type="button"
      aria-pressed={selected}
      aria-label={selected ? DESELECT_LABEL : SELECT_LABEL}
      onClick={(event) => {
        onToggleSelect({ range: event.shiftKey });
      }}
      className={`absolute left-0.5 top-0.5 z-10 flex h-8 w-8 items-center justify-center rounded-md transition-opacity focus:opacity-100 focus:outline-none focus:ring-2 focus:ring-accent ${revealed}`}
    >
      <span
        className={`flex h-4 w-4 items-center justify-center rounded border ${
          selected ? "border-accent bg-accent text-white" : "border-border bg-card"
        }`}
      >
        {selected ? <Check className="h-3 w-3" aria-hidden="true" /> : null}
      </span>
    </button>
  );
}
