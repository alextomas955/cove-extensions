/**
 * One catalogue scene the library does not hold.
 *
 * Hand-built on the host's own utility classes rather than through a host card component, which is
 * not exported to extensions. The cover is fetched by the browser straight from the provider's own
 * address: nothing is proxied and nothing is stored.
 */
import { StateChip } from "../common/ui/StateChip";
import type { MissingCard as MissingCardView } from "../wire/api";
import {
  CARD_BODY_CLASS,
  CARD_CLASS,
  CARD_MEDIA_CLASS,
  CARD_META_CLASS,
  CARD_TITLE_CLASS,
} from "./missingClasses";

/**
 * The name this tab gives a monitored scene.
 *
 * The underlying state is unchanged and so are its glyph and tint; only the word differs, because a
 * scene the library does not hold is one the reader is waiting for rather than one being watched.
 */
const MONITORED_LABEL = "Wanted";

export function MissingCard({ card }: { card: MissingCardView }) {
  return (
    <article className={CARD_CLASS}>
      <div className={CARD_MEDIA_CLASS}>
        {card.coverUrl === null ? null : (
          <img
            src={card.coverUrl}
            alt={card.title}
            loading="lazy"
            className="h-full w-full object-cover"
          />
        )}
      </div>
      <div className={CARD_BODY_CLASS}>
        <h3 className={CARD_TITLE_CLASS}>{card.title}</h3>
        <div className={CARD_META_CLASS}>
          {card.releaseDate === null ? null : <span>{card.releaseDate}</span>}
          {card.studioName === null ? null : <span>{card.studioName}</span>}
        </div>
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
