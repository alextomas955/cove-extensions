/**
 * The class strings the catalogue card and its grid are built from.
 *
 * Every visual is a host-emitted utility, because an extension stylesheet is page-global and would
 * leak onto every host page. Each string is declared once here and read, so a correction reaches
 * every caller.
 */

/**
 * The card surface.
 *
 * `rounded` and not `rounded-xl`: under the glass component style the host declares a `border`
 * shorthand on `.rounded-xl.border.border-border`, which resets border colour and box shadow and
 * would clobber both the accent border and the selection ring. Cove's own card dodges it the same
 * way.
 */
export const CARD_CLASS =
  "video-card group relative flex h-full flex-col overflow-hidden rounded border border-border bg-card text-left";

/**
 * The same surface while the card is ticked.
 *
 * Selection is carried by a ring, which is a box shadow, so it survives any redeclaration of the
 * border colour.
 */
export const CARD_SELECTED_CLASS =
  "video-card group relative flex h-full flex-col overflow-hidden rounded border border-accent bg-card text-left ring-2 ring-accent";

/** The cover box, which reserves its space before the image arrives. */
export const CARD_MEDIA_CLASS = "card-media relative aspect-video overflow-hidden bg-black";

/** The card's text column. */
export const CARD_BODY_CLASS =
  "card-body px-2.5 pt-2 pb-2 border-t border-border/50 flex-1 flex flex-col gap-1.5 min-h-0";

/** The title, clamped to two lines. */
export const CARD_TITLE_CLASS =
  "card-title font-semibold text-foreground line-clamp-2 group-hover:text-accent transition-colors leading-snug";

/** The date and studio row beneath the title. */
export const CARD_META_CLASS = "mt-1 flex items-center gap-2 text-[11px] text-muted";

/** The grid the cards sit in. */
export const GRID_CLASS = "grid gap-3";

/**
 * The grid's own columns, as an inline style.
 *
 * Inline rather than a utility: the responsive column classes Cove's own grid uses are absent from
 * the host stylesheet and would render nothing, and the class check inspects only arbitrary-value
 * classes so it would report neither. The minimum is the width the host sets on its own card.
 */
export const GRID_TEMPLATE_COLUMNS = "repeat(auto-fill, minmax(var(--card-min-width, 240px), 1fr))";
