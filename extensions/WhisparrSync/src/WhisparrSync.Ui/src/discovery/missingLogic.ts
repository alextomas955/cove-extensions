/**
 * Pure, DOM-free logic behind the per-entity Missing tab: the `/discovery/entity` request-body shaper, the
 * wire→row-model mapping, the poster-present/absent decision, the meta-segment assembly, and the entity
 * display-name derivation. Kept import-free (no React, no DOM, no SDK) so the offline gate can compile it in
 * isolation exactly like sceneStatusLogic.ts. The wire types live in contracts.ts and are imported as types
 * (erased at runtime), so this module stays offline-gate-clean.
 */

import type {
  DiscoveryFacetOption,
  DiscoveryFacetOptions,
  DiscoveryResult,
  EntityKind,
  MissingDiscoverySource,
  MissingDiscoveryState,
  MissingFacetAxis,
  MissingPerformer,
  MissingScene,
  MissingSceneStatus,
  MissingSortMode,
} from "../contracts";

/**
 * The rendered row model derived from one wire {@link MissingScene}. `posterUrl` is null when the scene
 * carries no poster (the view renders a fallback tile — zero-one-many); `meta` holds only the segments the
 * scene actually reports (a missing release date or entity name is omitted, never a dangling separator).
 * `releaseDate` is retained separately from the display `meta` so the sort comparators can key on it
 * (null when the scene reports none — those rows sort last under the date modes).
 */
export interface MissingRow {
  sourceId: string;
  title: string;
  posterUrl: string | null;
  // The landscape cover the card renders (falls back to posterUrl, then a tile); null when the scene reports none.
  coverUrl: string | null;
  // The scene's studio for the card meta line; null when absent.
  studioName: string | null;
  releaseDate: string | null;
  meta: string[];
  // The scene's performers (name + avatar), rendered as inline chips; the tags surface as a footer count. Both
  // empty when the source legitimately omits them — the card omits the strip/count, never an empty one.
  performers: MissingPerformer[];
  tags: string[];
  // The scene blurb the card's 2-line description renders; null when the source carries none.
  overview: string | null;
  // The Whisparr status the always-on pill renders. An omitted wire field defaults "notAdded", which is correct
  // only for a payload predating the abstaining value — a server that cannot assert a status sends it explicitly.
  status: MissingSceneStatus;
  // CLIENT-optimistic wanted flag: flipped when the card's Monitor persists, reverted if it fails. The
  // server holds no wanted-intent of its own, so this does not survive a reload. Default false.
  wanted: boolean;
}

/**
 * The status wording the card renders — keyed on the pinned wire strings (the drift check). `unknown` reads as
 * an open question, never as a variant of "Not added": telling "we cannot know" apart from "there is nothing"
 * at a glance is the whole point of the fourth value.
 */
export const MISSING_STATUS_LABEL: Record<MissingSceneStatus, string> = {
  notAdded: "Not added",
  wanted: "Wanted",
  unmonitored: "Unmonitored",
  unknown: "Status unknown",
};

/**
 * The sentence an abstaining row carries at its glyph when the connected generation cannot correlate a scene to
 * a Whisparr row at all. It names a limitation of the provider on the other end, never a Cove one, and it names
 * no remedy because none exists — both Whisparr generations are first-class, and the wording implies no
 * migration. It lives beside {@link MISSING_STATUS_LABEL} because the discovery slice is its only consumer; the
 * sibling abstention for the folder-overlap check is composed in `settings/folderOverlapLogic.ts`,
 * deliberately separate.
 */
export const MISSING_STATUS_UNKNOWN_REASON =
  "The connected Whisparr can't report a per-scene status here, so Cove doesn't guess.";

/**
 * The visual a missing status maps onto: the three {@link SceneWhisparrState} keys whose videos-page glyph the
 * card borrows, plus the indeterminate key that has no counterpart there.
 */
export type MissingStatusVisualKey = "notAdded" | "monitored" | "unmonitored" | "unknown";

/**
 * Map a missing status onto the videos-page management state whose glyph it borrows, so the discovery card and
 * the native videos card paint the SAME status glyph: a wanted scene is monitored (a filled accent bookmark),
 * unmonitored stays unmonitored, and not-added stays not-added. The DISPLAY wording keeps the discovery
 * vocabulary ({@link MISSING_STATUS_LABEL}); only the glyph is shared. The abstaining status maps to its own
 * key, which the VIEW resolves to the standalone `UNKNOWN_STATE_VISUAL` descriptor: the enum-keyed record is
 * keyed on a pinned wire enum, and a fifth member there would ripple into the scene-status projection.
 */
export function missingStatusVisualKey(status: MissingSceneStatus): MissingStatusVisualKey {
  switch (status) {
    case "wanted":
      return "monitored";
    case "unmonitored":
      return "unmonitored";
    case "unknown":
      return "unknown";
    default:
      return "notAdded";
  }
}

/**
 * The sentence an abstaining row carries at its glyph. An abstention has two causes and never one sentence for
 * both: where the connected generation cannot correlate a scene to a Whisparr row at all the cause is a
 * permanent provider limitation ({@link MISSING_STATUS_UNKNOWN_REASON}); where it can, an abstention means the
 * Whisparr movie-set read did not answer — a transient outage with a retry as its remedy, worded by the
 * caller-supplied `unreachableCopy`. A status the server actually asserted has no reason and returns null. The
 * outage sentence arrives as a PARAMETER (mirroring `common/lib/actionFailureLogic.ts`'s version-capability
 * argument), which keeps this module import-free for the offline gate.
 */
export function missingStatusReason(
  status: MissingSceneStatus,
  perSceneStatusReportable: boolean,
  unreachableCopy: string,
): string | null {
  switch (status) {
    case "unknown":
      return perSceneStatusReportable ? unreachableCopy : MISSING_STATUS_UNKNOWN_REASON;
    case "notAdded":
    case "wanted":
    case "unmonitored":
      return null;
  }
}

/**
 * Whether the movie-set outage sentence is drawn ONCE over the whole set. The two abstention causes are drawn in
 * two different places: where a per-scene status is reportable, the only way a row can abstain is that the
 * Whisparr movie-set read did not answer, so the cause belongs to the set and is drawn over it, never as a
 * verdict repeated on every card; where a per-scene status is not reportable at all, the abstention is a
 * permanent provider limitation rather than a failure, so nothing is drawn over the set — a banner there would
 * call a standing limitation transient and offer a Refresh that can never clear it, and the per-row glyph carries
 * the cause instead.
 *
 * The verdict reads the RENDERED rows, so a zero-row result draws nothing. That is deliberate rather than a gap:
 * the movie-set read decides a scene's STATUS, not the set's MEMBERSHIP, so with no rows there is no per-item
 * verdict an outage could corrupt and the own-everything reading stays true whether or not Whisparr answered.
 */
export function movieSetOutageDrawn(
  rows: readonly { readonly status: MissingSceneStatus }[],
  perSceneStatusReportable: boolean,
): boolean {
  return perSceneStatusReportable && rows.some((row) => row.status === "unknown");
}

/** Where a whole set's abstention is stated: over the set, or on each row that abstains — never both. */
export interface MissingStatusAbstention {
  setReason: string | null;
  perCardReasonDrawn: boolean;
}

/**
 * Where the abstention of a set of rows is stated, and with what.
 *
 * On a generation carrying no scene-level Whisparr row, the dimmed verbs and the unreported statuses are ONE fact
 * with one cause, so one sentence names both consequences; drawing them separately would state the same cause
 * twice on a surface. The two shipped literals arrive composed rather than restated so the wording they are
 * single-sourced for cannot drift, and the sentence names no remedy because there is none — both generations are
 * first-class.
 *
 * `capabilityCopy` is a PARAMETER for the reason `missingStatusReason`'s `unreachableCopy` is: this module stays
 * import-free of copy so the offline gate compiles it standalone.
 */
export function missingStatusAbstention(
  rows: readonly { readonly status: MissingSceneStatus }[],
  perSceneStatusReportable: boolean,
  capabilityCopy: string,
): MissingStatusAbstention {
  const setWide =
    !perSceneStatusReportable &&
    rows.length > 0 &&
    // `every`, not `some`: a set-wide claim untrue of some row would replace a set of correct per-row statements
    // with one wrong one, so a mixed set keeps the per-row form.
    rows.every((row) => row.status === "unknown");
  return setWide
    ? {
        setReason: `Monitor, Unmonitor and Search act on a scene-level Whisparr row, which this connection has none of — ${capabilityCopy}. ${MISSING_STATUS_UNKNOWN_REASON}`,
        perCardReasonDrawn: false,
      }
    : { setReason: null, perCardReasonDrawn: true };
}

/**
 * The sentence the Search control carries after a click that did not search. Three causes reach this one control
 * and they never share a message: the connected generation cannot search per-scene at all, where the disabled
 * control's own title already carries the shipped capability copy, so no second sentence is produced here; the
 * Whisparr movie-set read did not answer, where the set-level banner owns the cause and the per-item sentence is
 * suppressed ENTIRELY — an outage is drawn once over the set, never as a per-card verdict; and Whisparr answered
 * and holds no entry for this scene yet, the only cause this derivation words, from the caller-supplied
 * `notAddedCopy`, which names a remedy the user can perform. A click that reported `searched:true`, and a row
 * nobody has clicked yet, both carry nothing. The sentence is a PARAMETER, never an import, so this module stays
 * offline-gate-clean.
 */
export function searchRefusalReason(
  searched: boolean | undefined,
  status: MissingSceneStatus,
  perSceneSearchable: boolean,
  notAddedCopy: string,
): string | null {
  if (!perSceneSearchable) {
    return null;
  }
  if (status === "unknown") {
    return null;
  }
  if (searched === undefined) {
    return null;
  }
  return searched ? null : notAddedCopy;
}

/**
 * The PascalCase query fragment the read request and BOTH action requests carry — the ordering and the facet
 * narrowing asked of the metadata provider. Every member is optional and a member at its default is OMITTED, so
 * the fragment itself is absent for a default view and the shipped body is reproduced byte for byte.
 *
 * A page index alone stops naming a re-derivable set once an ordering exists: page three of one ordering and
 * page three of another are disjoint sets. Both halves of the coordinate therefore ride every request.
 */
export interface DiscoveryQueryFields {
  Sort?: MissingSortMode;
  StudioId?: string;
  PerformerId?: string;
  TagId?: string;
  Year?: number;
}

/**
 * The PascalCase `/discovery/entity` request body — the Cove entity id + kind; the server resolves the entity's
 * remote id. `page` (1-based) is OMITTED for a whole-catalogue re-derive and present for a server-paged read
 * (one page per request — the direct source serves that page; a whole-catalogue response ignores it). `query`
 * is omitted for a default view; a pristine read's body is then byte-identical to the shipped one.
 */
export function discoveryEntityBody(
  kind: EntityKind,
  coveEntityId: number,
  page?: number,
  query?: DiscoveryQueryFields,
): { CoveEntityId: number; Kind: EntityKind; Page?: number; Query?: DiscoveryQueryFields } {
  const body = { CoveEntityId: coveEntityId, Kind: kind };
  const paged = page === undefined ? body : { ...body, Page: page };
  return query === undefined ? paged : { ...paged, Query: query };
}

/** The first (1-based) catalogue page an incremental read requests. */
export const FIRST_PAGE = 1;

/**
 * The discovery paging cursor the store carries between loads: the next page to request (null when the
 * catalogue is exhausted) and whether a further page remains.
 */
export interface MissingCursor {
  nextPage: number | null;
  hasMore: boolean;
}

/**
 * The cursor derived from a page response — the server's `nextPage`/`hasMore`, defaulting to exhausted when the
 * payload omits them (a whole-catalogue/non-paged or older response never advertises more).
 */
export function advanceCursor(
  result: Pick<DiscoveryResult, "nextPage" | "hasMore">,
): MissingCursor {
  return {
    nextPage: result.nextPage ?? null,
    hasMore: result.hasMore ?? false,
  };
}

/**
 * The fixed page size the Missing grid renders one page of — matches the server's fixed `DirectPageSize`, so a
 * server-paged read and the client-side slice of a whole-set catalogue divide into the same 40-per-page pages.
 */
export const MISSING_PAGE_SIZE = 40;

/** The number of pages a `total`-row list divides into at `size` rows per page — never below 1 (an empty list is page 1 of 1). */
export function pageCount(total: number, size = MISSING_PAGE_SIZE): number {
  return Math.max(1, Math.ceil(total / size));
}

/**
 * The 1-based `page`-th slice of `rows` at `size` rows per page — `rows[(page-1)*size .. page*size)`. An
 * over-range page yields an empty slice; the input array is never mutated (a whole-set catalogue slices here).
 */
export function pageSlice<T>(rows: readonly T[], page: number, size = MISSING_PAGE_SIZE): T[] {
  const start = (page - 1) * size;
  return rows.slice(start, start + size);
}

/** Clamp a requested page into `[1, totalPages]` so an over/under-range navigation lands on a real page. */
export function clampPage(page: number, totalPages: number): number {
  return Math.max(1, Math.min(totalPages, page));
}

/** The toolbar count-label parts: the 1-based `start`/`end` the current page spans, and the `total` it is of. */
export interface MissingCountRange {
  start: number;
  end: number;
  total: number;
  /** Whether `total` is a lower bound because the source stopped counting (rendered with a trailing "+"). */
  totalIsAtLeast: boolean;
  /**
   * Whether the source catalogue READ was cut short by its page ceiling. Distinct from `totalIsAtLeast`: that
   * one says the source stopped counting, this one says rows are absent from the diff entirely, so scenes can
   * be missing from "missing".
   */
  truncated: boolean;
}

/**
 * The toolbar count-label parts in Cove's "start-end of total" convention. `total` is the whole catalogue size
 * (the tab-badge number), not the current page's rendered row count, so the label stays stable across pages;
 * `start` is 0 for an empty catalogue and `end` clamps to `total` on a short final page.
 */
export function missingCountRange(
  total: number,
  page: number,
  size = MISSING_PAGE_SIZE,
  totalIsAtLeast = false,
  truncated = false,
): MissingCountRange {
  const start = total > 0 ? (page - 1) * size + 1 : 0;
  const end = Math.min(page * size, total);
  return { start, end, total, totalIsAtLeast, truncated };
}

/**
 * The one sentence shown when the catalogue read was cut short, or null. Kept separate from the count label so
 * it is said ONCE per screen rather than folded into a string that repeats per control, and kept here so the
 * wording is asserted without a DOM.
 */
export function missingTruncationNotice(count: MissingCountRange): string | null {
  return count.truncated
    ? "The metadata source returned only part of this catalogue, so some missing scenes are not listed."
    : null;
}

/**
 * The count label: "1-40 of 1,772", or "1-40 of 10,000+" when the source stopped counting. Rendering a saturated
 * total as exact would overstate what the source actually reported.
 */
export function missingCountLabel(count: MissingCountRange): string {
  if (count.total <= 0) {
    return "0 missing";
  }
  const total = `${count.total.toLocaleString()}${count.totalIsAtLeast ? "+" : ""}`;
  return `${count.start.toString()}-${count.end.toString()} of ${total}`;
}

/** The pill state a discovery Monitor/Unmonitor flip stamps onto a row — the wanted flag and its status. */
export type MissingFlipState = Pick<MissingRow, "wanted" | "status">;

/**
 * Stamp a uniform optimistic {@link MissingFlipState} onto the target rows, returning NEW rows (input never mutated);
 * a non-target row is copied through unchanged. The store's optimistic monitor/unmonitor flip renders through this.
 */
export function stampWantedRows(
  rows: readonly MissingRow[],
  targetIds: readonly string[],
  over: MissingFlipState,
): MissingRow[] {
  const targets = new Set(targetIds);
  return rows.map((row) => (targets.has(row.sourceId) ? { ...row, ...over } : row));
}

/**
 * The state a FAILED flip restores a target row to. Both intents restore the prior status (notAdded when the row
 * reported none); an "add" (monitor) failure returns the row to not-wanted, a "delete" (unmonitor) failure to its
 * prior wanted flag. An absent prior (the row is gone) is a not-added, not-wanted row.
 */
function revertFlipState(
  prior: MissingFlipState | undefined,
  intent: "add" | "delete",
): MissingFlipState {
  const status = prior?.status ?? "notAdded";
  return intent === "add" ? { wanted: false, status } : { wanted: prior?.wanted ?? false, status };
}

/**
 * Revert the target rows to their pre-flip state after a failed persist, returning NEW rows (input never mutated).
 * The caller passes the CURRENT rows, NOT the pre-flip snapshot — so a flip made against rows a concurrent refresh
 * has since replaced still reverts wherever the target row now lives, and a target no longer present is a no-op.
 */
export function revertWantedRows(
  rows: readonly MissingRow[],
  targetIds: readonly string[],
  prior: ReadonlyMap<string, MissingFlipState>,
  intent: "add" | "delete",
): MissingRow[] {
  const targets = new Set(targetIds);
  return rows.map((row) =>
    targets.has(row.sourceId)
      ? { ...row, ...revertFlipState(prior.get(row.sourceId), intent) }
      : row,
  );
}

/** The discovery-action ops the card/selection surfaces dispatch. Only "search" issues an immediate grab. */
export type DiscoveryActionOp = "monitor" | "unmonitor" | "search";

/**
 * The page coordinate an action may send, or undefined to omit it. A server-paged read fetched exactly the rows
 * on screen, so its page number names the same set when the server re-derives it. A whole-catalogue response is
 * sliced client-side, so the same number is a slice index that names a DIFFERENT set server-side — sending it
 * would fail the server's membership check and refuse an action that is perfectly valid. Omitting it is always
 * safe: an absent page is the whole-catalogue re-derive the server has always done.
 */
export function actionPageCoordinate(page: number, serverPaged: boolean): number | undefined {
  return serverPaged ? page : undefined;
}

/**
 * The PascalCase `/discovery/action` request body: the op + the entity context + the client-held stable source
 * id (the server validates it against the re-derived missing set). Matches the shipped sibling request record
 * (`/discovery/entity`) — a request body is PascalCase; camelCase is response-only. An omitted `page` re-derives
 * the whole catalogue (the shipped behaviour); a supplied one bounds the re-derive to that page and is a
 * coordinate the server clamps and validates rather than trusts.
 * `query` completes that coordinate. The server re-derives the page under the ordering the row was rendered
 * under; without it, page three of one ordering would be checked against page three of another.
 */
export function actionRequestBody(
  op: DiscoveryActionOp,
  kind: EntityKind,
  coveEntityId: number,
  sourceId: string,
  page?: number,
  query?: DiscoveryQueryFields,
): {
  Op: DiscoveryActionOp;
  CoveEntityId: number;
  Kind: EntityKind;
  SourceId: string;
  Page?: number;
  Query?: DiscoveryQueryFields;
} {
  const body = { Op: op, CoveEntityId: coveEntityId, Kind: kind, SourceId: sourceId };
  const paged = page === undefined ? body : { ...body, Page: page };
  return query === undefined ? paged : { ...paged, Query: query };
}

/**
 * The PascalCase `/discovery/action-all` body. `SourceIds` is OMITTED for a whole-entity mark-all, present for a
 * validated selection subset (the server intersects it with the re-derived missing set). An omitted `page`
 * re-derives the whole catalogue; a supplied one bounds the re-derive to the page a selection was made on and is
 * a coordinate the server clamps and validates.
 *
 * A whole-entity mark-all drops any page it is handed, rather than forwarding it: the whole set is the entire
 * point of a mark-all, so bounding it to one page would silently shrink the operation to the rows currently on
 * screen. Dropping it here means no caller can build that body. The query is dropped on the same grounds and for
 * the same reason — a narrowed mark-all is a smaller operation than the control names.
 */
export function actionAllBody(
  op: DiscoveryActionOp,
  kind: EntityKind,
  coveEntityId: number,
  sourceIds?: readonly string[],
  page?: number,
  query?: DiscoveryQueryFields,
): {
  Op: DiscoveryActionOp;
  CoveEntityId: number;
  Kind: EntityKind;
  SourceIds?: string[];
  Page?: number;
  Query?: DiscoveryQueryFields;
} {
  const body = { Op: op, CoveEntityId: coveEntityId, Kind: kind };
  if (sourceIds === undefined) {
    return body;
  }
  const withSelection = { ...body, SourceIds: [...sourceIds] };
  const paged = page === undefined ? withSelection : { ...withSelection, Page: page };
  return query === undefined ? paged : { ...paged, Query: query };
}

/**
 * Whether the whole-entity "Monitor all" is offered for this kind.
 *
 * A tag spans the entire library, not one studio's or performer's output: tens of thousands of scenes. Marking
 * every missing one wanted in a single click is neither intendable nor undoable at that size. The bounded per-card
 * and multi-selection Monitor stay available on every kind.
 */
export function monitorAllOffered(kind: EntityKind): boolean {
  return kind !== "tag";
}

/** Toggle one card's membership in the selection, returning a NEW Set (never mutates the input). */
export function toggleSelected(selected: ReadonlySet<string>, sourceId: string): Set<string> {
  const next = new Set(selected);
  if (next.has(sourceId)) {
    next.delete(sourceId);
  } else {
    next.add(sourceId);
  }
  return next;
}

/** A NEW Set of every currently-visible (filter-matched) row's sourceId — select-all over what is shown. */
export function selectAllVisible(visibleRows: readonly MissingRow[]): Set<string> {
  return new Set(visibleRows.map((row) => row.sourceId));
}

/** A fresh empty selection. */
export function clearSelection(): Set<string> {
  return new Set();
}

export function invertSelection(
  selected: ReadonlySet<string>,
  visibleRows: readonly MissingRow[],
): Set<string> {
  const next = new Set<string>();
  for (const row of visibleRows) {
    if (!selected.has(row.sourceId)) {
      next.add(row.sourceId);
    }
  }
  return next;
}

/**
 * How many selected ids are still present among the visible rows — the badge count. A filter/refresh that hides
 * a selected row reconciles the count (a stale id no longer visible stops being counted) without pruning the set.
 */
export function selectedVisibleCount(
  selected: ReadonlySet<string>,
  visibleRows: readonly MissingRow[],
): number {
  let count = 0;
  for (const row of visibleRows) {
    if (selected.has(row.sourceId)) {
      count++;
    }
  }
  return count;
}

/** A non-empty trimmed string, or null — the single guard the row shaping applies to every optional wire field. */
function present(value: string | null | undefined): string | null {
  return typeof value === "string" && value.trim().length > 0 ? value : null;
}

/** The meta line's segments (release date, then entity name) — each omitted when the scene does not report it. */
export function metaSegments(scene: MissingScene): string[] {
  const segments: string[] = [];
  const releaseDate = present(scene.releaseDate);
  if (releaseDate) segments.push(releaseDate);
  const entityName = present(scene.entityName);
  if (entityName) segments.push(entityName);
  return segments;
}

/** Map one wire scene into its row model: a present cover/poster yields an image row, an absent one a fallback tile. */
export function missingRow(scene: MissingScene): MissingRow {
  return {
    sourceId: scene.sourceId,
    title: present(scene.title) ?? "Untitled scene",
    posterUrl: present(scene.posterUrl),
    coverUrl: present(scene.coverUrl),
    studioName: present(scene.studioName),
    releaseDate: present(scene.releaseDate),
    meta: metaSegments(scene),
    performers: scene.performers ?? [],
    tags: scene.tags ?? [],
    overview: present(scene.overview),
    status: scene.status ?? "notAdded",
    wanted: false,
  };
}

/** Map the wire scene list into the rendered row models, order preserved. */
export function toMissingRows(scenes: readonly MissingScene[]): MissingRow[] {
  return scenes.map(missingRow);
}

/**
 * The entity's display name for the meta line + empty-state copy: the server-reported name when present, else
 * a per-kind generic fallback so the copy never renders a blank name.
 */
export function entityDisplayName(entityName: string | null | undefined, kind: EntityKind): string {
  const fallback =
    kind === "studio" ? "this studio" : kind === "tag" ? "this tag" : "this performer";
  return present(entityName) ?? fallback;
}

/**
 * Case-insensitive substring match of the row title against a query. An empty or whitespace-only query
 * matches every row (no filter applied), so the controls default to showing the full list.
 */
export function matchesTitleQuery(row: MissingRow, query: string): boolean {
  const needle = query.trim().toLowerCase();
  if (needle.length === 0) return true;
  return row.title.toLowerCase().includes(needle);
}

/**
 * Order two rows by release date. Absent dates sort LAST under both directions (a missing date is not
 * "oldest") — never a comparator throw. ISO date strings compare correctly lexicographically.
 */
function compareByReleaseDate(a: MissingRow, b: MissingRow, ascending: boolean): number {
  if (a.releaseDate === null && b.releaseDate === null) return 0;
  if (a.releaseDate === null) return 1;
  if (b.releaseDate === null) return -1;
  const diff = a.releaseDate.localeCompare(b.releaseDate);
  return ascending ? diff : -diff;
}

/** The comparator for a sort mode: newest-first (date desc), oldest-first (date asc), or title A–Z. */
function comparatorFor(sortMode: MissingSortMode): (a: MissingRow, b: MissingRow) => number {
  switch (sortMode) {
    case "oldest":
      return (a, b) => compareByReleaseDate(a, b, true);
    case "title":
      return (a, b) => a.title.localeCompare(b.title, undefined, { sensitivity: "base" });
    case "newest":
    default:
      return (a, b) => compareByReleaseDate(a, b, false);
  }
}

/** The leading 4-digit year of a release date (`"2021-03-03"` → `"2021"`), or null when absent/unparseable. */
function leadingYear(releaseDate: string | null): string | null {
  if (releaseDate === null) return null;
  const match = /^(\d{4})/.exec(releaseDate);
  return match ? match[1] : null;
}

// Case-insensitive so "Ada" and "ada" collapse to one facet option, not two adjacent ones.
function byLabel(a: string, b: string): number {
  return a.localeCompare(b, undefined, { sensitivity: "base" });
}

/**
 * One selectable facet value. `id` is the PROVIDER's own filter id when the option came from a source that
 * supplies one, and **null** when it was read off the loaded rows. The null IS the statement "this option
 * cannot narrow the whole set", held at the type level where no reader can forget to branch on it.
 */
export interface MissingFacetOption {
  id: string | null;
  label: string;
}

/** The facet values a control offers, per axis. */
export interface MissingFacetOptions {
  studios: MissingFacetOption[];
  performers: MissingFacetOption[];
  tags: MissingFacetOption[];
  years: MissingFacetOption[];
}

/**
 * Distinct, sorted facet values across the loaded rows. A facet list is empty when no row carries it — a
 * source that omits performers/tags yields those two empty, not absent. Every id is null: a value read off a
 * rendered row names no provider id, and pretending otherwise is what the null is here to prevent.
 */
export function deriveFacetOptions(rows: readonly MissingRow[]): MissingFacetOptions {
  const studios = new Set<string>();
  const performers = new Set<string>();
  const tags = new Set<string>();
  const years = new Set<string>();
  for (const row of rows) {
    if (row.studioName !== null) studios.add(row.studioName);
    for (const performer of row.performers) performers.add(performer.name);
    for (const tag of row.tags) tags.add(tag);
    const year = leadingYear(row.releaseDate);
    if (year !== null) years.add(year);
  }
  const rowOptions = (values: Set<string>, sorter: (a: string, b: string) => number) =>
    [...values].sort(sorter).map((label) => ({ id: null, label }));
  return {
    studios: rowOptions(studios, byLabel),
    performers: rowOptions(performers, byLabel),
    tags: rowOptions(tags, byLabel),
    // Descending: recent years lead, aligned with the newest-first default sort.
    years: rowOptions(years, (a, b) => b.localeCompare(a)),
  };
}

/**
 * The options each control offers: the server's list for an axis it supplied, else the row-derived one.
 *
 * The two are not interchangeable — a server option carries a provider id and can narrow the whole catalogue,
 * a row-derived one carries null and can only filter what is on screen — which is why the choice is made once,
 * here, and every reader downstream sees one shape.
 */
export function mergeFacetOptions(
  server: DiscoveryFacetOptions | null | undefined,
  rows: readonly MissingRow[],
): MissingFacetOptions {
  const derived = deriveFacetOptions(rows);
  if (server === null || server === undefined) return derived;
  const pick = (
    supplied: DiscoveryFacetOption[] | undefined,
    fallback: MissingFacetOption[],
  ): MissingFacetOption[] => (supplied !== undefined && supplied.length > 0 ? supplied : fallback);
  return {
    studios: pick(server.studios, derived.studios),
    performers: pick(server.performers, derived.performers),
    tags: pick(server.tags, derived.tags),
    years: pick(server.years, derived.years),
  };
}

/**
 * Which facet a control targets — one of the four cross-cutting axes (never the tab's own entity axis). It is
 * the wire axis vocabulary itself ({@link MissingFacetAxis}), which is what stops a control key, a filter-state
 * field and a request field from drifting into three spellings of one axis.
 */
export type MissingFacetKey = MissingFacetAxis;

/** One rendered facet control: its selection key, a human label, and the option values it offers. */
export interface MissingFacetDescriptor {
  key: MissingFacetKey;
  label: string;
  options: MissingFacetOption[];
}

/**
 * The context-aware, ordered facet controls for a tab of the given entity kind. The tab's own (fixed) entity
 * axis is never a facet — it would discriminate nothing. The CROSS-CUTTING axes lead instead: a performer page
 * offers studio + tag, a studio page offers performer + tag, and a tag page offers performer + studio (its own
 * tag axis is omitted), each trailed by year. A facet with no option values is dropped, so a source that omits
 * performers/tags shows only the facets its data supports.
 *
 * A PARENT studio (`isParent`) is the one case where the studio axis IS offered on a studio page: its catalogue
 * aggregates every child sub-studio, so its own studio axis becomes a real narrowing dimension. It leads the
 * set, labeled for the sub-studio it selects, reusing the same `studio` key so the shipped predicate and URL
 * param apply unchanged; a non-parent studio omits it exactly as before.
 *
 * The zero-option drop cuts a second way once an aggregate is in play: it can populate an axis the loaded rows
 * carry nothing for, which makes a control appear on a page that would otherwise have shown none.
 */
export function facetsForKind(
  kind: EntityKind,
  options: MissingFacetOptions,
  isParent = false,
): MissingFacetDescriptor[] {
  const performer: MissingFacetDescriptor = {
    key: "performer",
    label: "Performer",
    options: options.performers,
  };
  const studio: MissingFacetDescriptor = {
    key: "studio",
    label: "Studio",
    options: options.studios,
  };
  const subStudio: MissingFacetDescriptor = { ...studio, label: "Sub-studio" };
  const tag: MissingFacetDescriptor = { key: "tag", label: "Tag", options: options.tags };
  const year: MissingFacetDescriptor = { key: "dateYear", label: "Year", options: options.years };
  const ordered: MissingFacetDescriptor[] =
    kind === "performer"
      ? [studio, tag, year]
      : kind === "tag"
        ? [performer, studio, year]
        : isParent
          ? [subStudio, performer, tag, year]
          : [performer, tag, year];
  return ordered.filter((facet) => facet.options.length > 0);
}

/**
 * The Missing tab's bookmarkable filter state — the title query, the sort mode, and the four facet selections
 * (each null when unset). The facet selections are the cross-cutting axes; the tab's own entity axis is never
 * one of them (see {@link facetsForKind}).
 */
export interface MissingFilterState {
  query: string;
  sortMode: MissingSortMode;
  studio: string | null;
  performer: string | null;
  tag: string | null;
  dateYear: string | null;
}

/** Whether a row satisfies every active facet selection — a null selection is a no-op (matches every row). */
function matchesFacets(row: MissingRow, state: MissingFilterState): boolean {
  if (state.studio !== null && row.studioName !== state.studio) return false;
  if (state.performer !== null && !row.performers.some((p) => p.name === state.performer)) {
    return false;
  }
  if (state.tag !== null && !row.tags.includes(state.tag)) return false;
  if (state.dateYear !== null && leadingYear(row.releaseDate) !== state.dateYear) return false;
  return true;
}

/** The rendered row set: apply the title query and the facet predicates, THEN sort (pure, order-preserving). */
export function visibleMissingRows(
  rows: readonly MissingRow[],
  state: MissingFilterState,
): MissingRow[] {
  const filtered = rows.filter(
    (row) => matchesTitleQuery(row, state.query) && matchesFacets(row, state),
  );
  return filtered.sort(comparatorFor(state.sortMode));
}

/** The locked default sort — newest release first (see contracts.ts / the sort control). */
const DEFAULT_SORT_MODE: MissingSortMode = "newest";

/**
 * The query fragment for a filter state, or undefined when every dimension sits at its default. Undefined is
 * what keeps a pristine read's body byte-identical to the shipped one.
 *
 * A facet selection is a LABEL — that is what the URL carries and what the control shows — and the provider
 * filters by id, so each active selection is resolved against `options`. A label that resolves sends its id and
 * the provider narrows the whole catalogue. A label that does not — a hand-edited URL, a bookmark from another
 * entity, an option the current read never offered — sends NOTHING for that axis, which leaves the shipped
 * client-side predicate to narrow the loaded rows and leaves the axis reporting itself degraded. One code path,
 * honest in both directions, and no bookmark stops working.
 */
export function discoveryQueryFields(
  state: MissingFilterState,
  options?: MissingFacetOptions,
): DiscoveryQueryFields | undefined {
  const fields: DiscoveryQueryFields = {};
  if (state.sortMode !== DEFAULT_SORT_MODE) fields.Sort = state.sortMode;

  const studio = resolveFilterId(state.studio, options?.studios);
  if (studio !== null) fields.StudioId = studio;
  const performer = resolveFilterId(state.performer, options?.performers);
  if (performer !== null) fields.PerformerId = performer;
  const tag = resolveFilterId(state.tag, options?.tags);
  if (tag !== null) fields.TagId = tag;

  // The year axis needs no option list: the selection IS the value the provider filters on, and both providers
  // take it as an integer. An unparseable selection is dropped exactly as an unresolvable label is.
  if (state.dateYear !== null) {
    const year = Number.parseInt(state.dateYear, 10);
    if (Number.isInteger(year)) fields.Year = year;
  }

  return Object.keys(fields).length === 0 ? undefined : fields;
}

/**
 * The follow-up read a restored bookmark needs, or undefined when it needs none.
 *
 * A facet selection is a LABEL — that is what the URL carries and what the control shows — and the list that
 * resolves it to a provider id arrives WITH the response, so the read that RESTORES a bookmark cannot carry
 * one. This names the second read, once that list has landed. Returning undefined where the resolved query
 * matches what `asked` already carried is what keeps a view needing no follow-up from costing a provider read.
 */
export function restoreFollowUpQuery(
  state: MissingFilterState,
  options: MissingFacetOptions,
  asked: DiscoveryQueryFields | undefined,
): DiscoveryQueryFields | undefined {
  const resolved = discoveryQueryFields(state, options);
  return sameQuery(resolved, asked) ? undefined : resolved;
}

// Compared member by member rather than by a JSON round-trip, which would call two equal queries different
// whenever their keys were inserted in a different order.
function sameQuery(
  a: DiscoveryQueryFields | undefined,
  b: DiscoveryQueryFields | undefined,
): boolean {
  if (a === undefined || b === undefined) return a === b;
  return (
    a.Sort === b.Sort &&
    a.StudioId === b.StudioId &&
    a.PerformerId === b.PerformerId &&
    a.TagId === b.TagId &&
    a.Year === b.Year
  );
}

// A selection's provider id, or null when it cannot be resolved to one. Matching is on the LABEL, which is what
// the URL and the control both carry; an option whose own id is null resolves to null, since a value read off a
// rendered row names nothing the provider can filter by.
function resolveFilterId(
  selection: string | null,
  options: readonly MissingFacetOption[] | undefined,
): string | null {
  if (selection === null || options === undefined) return null;
  return options.find((option) => option.label === selection)?.id ?? null;
}

// The URL params are NAMESPACED so writing the Missing tab's state into the host page URL never clobbers a
// host query param. The bookmarkable round-trip is the contract; the exact names are incidental.
const PARAM_QUERY = "wsMissingQ";
const PARAM_SORT = "wsMissingSort";
const PARAM_STUDIO = "wsMissingStudio";
const PARAM_PERFORMER = "wsMissingPerformer";
const PARAM_TAG = "wsMissingTag";
const PARAM_YEAR = "wsMissingYear";
const PARAM_PAGE = "wsMissingPage";

const DEFAULT_FILTER_STATE: MissingFilterState = {
  query: "",
  sortMode: DEFAULT_SORT_MODE,
  studio: null,
  performer: null,
  tag: null,
  dateYear: null,
};

/** A facet URL param → a selection: a present, non-blank value, else null (the unset default). */
function facetSelection(value: string | null): string | null {
  return value !== null && value.trim().length > 0 ? value : null;
}

/** The valid sort modes, so a hand-edited/unknown URL value falls back to the default rather than a bad mode. */
function parseSortMode(value: string | null): MissingSortMode {
  return value === "oldest" || value === "title" || value === "newest" ? value : DEFAULT_SORT_MODE;
}

/**
 * Parse the Missing tab's filter state from a host page search string (with or without the leading `?`).
 * Absent or unrecognized params fall back to the defaults, so a pristine or foreign URL yields the default view.
 */
export function readFilterStateFromSearch(search: string): MissingFilterState {
  const params = new URLSearchParams(search);
  return {
    query: params.get(PARAM_QUERY) ?? DEFAULT_FILTER_STATE.query,
    sortMode: parseSortMode(params.get(PARAM_SORT)),
    studio: facetSelection(params.get(PARAM_STUDIO)),
    performer: facetSelection(params.get(PARAM_PERFORMER)),
    tag: facetSelection(params.get(PARAM_TAG)),
    dateYear: facetSelection(params.get(PARAM_YEAR)),
  };
}

/**
 * Merge the Missing tab's filter state into an existing host search string, returning the new search string
 * (no leading `?`). Unrelated host params are preserved; a field at its default is REMOVED (never left stale),
 * so a pristine view produces a clean URL and the write→read round-trip is lossless.
 */
export function writeFilterStateToSearch(search: string, state: MissingFilterState): string {
  const params = new URLSearchParams(search);

  if (state.query.trim().length > 0) {
    params.set(PARAM_QUERY, state.query);
  } else {
    params.delete(PARAM_QUERY);
  }

  if (state.sortMode !== DEFAULT_SORT_MODE) {
    params.set(PARAM_SORT, state.sortMode);
  } else {
    params.delete(PARAM_SORT);
  }

  writeFacetParam(params, PARAM_STUDIO, state.studio);
  writeFacetParam(params, PARAM_PERFORMER, state.performer);
  writeFacetParam(params, PARAM_TAG, state.tag);
  writeFacetParam(params, PARAM_YEAR, state.dateYear);

  return params.toString();
}

/** Set a facet param when its selection is a non-blank value; drop it at its (null/blank) default. */
function writeFacetParam(params: URLSearchParams, name: string, value: string | null): void {
  if (value !== null && value.trim().length > 0) {
    params.set(name, value);
  } else {
    params.delete(name);
  }
}

/** The status of the last `/discovery/entity` fetch — a load in flight, an ok result, or an outage. */
export type MissingFetchStatus = "loading" | "ok" | "error";

/**
 * Which distinct block the Missing tab renders. Two load-bearing distinctness rules: `outage` (an unreachable
 * source) and `ownEverything` (an ok, empty catalogue) are NEVER the same state; and the three
 * server-decided unmonitored states — `needsProviderKey`, `noSourceId`, `sourceUnreachable` — are each their
 * own view, never collapsed into `ownEverything` or the whisparr `outage`.
 */
export type MissingView =
  | "loading"
  | "outage"
  | "ownEverything"
  | "noMatch"
  | "populated"
  | "needsProviderKey"
  | "noSourceId"
  | "sourceUnreachable";

/**
 * The pure inputs the view derivation reads — the fetch status, the already-computed row counts, and the
 * server-decided `state` (absent/`ok` on the served path). A thrown fetch surfaces as `fetchStatus:"error"`;
 * the non-ok discriminated states arrive as a normal `ok` fetch with zero rows plus the `state` field.
 */
export interface MissingViewInput {
  fetchStatus: MissingFetchStatus;
  totalRows: number;
  visibleCount: number;
  hasQuery: boolean;
  state?: MissingDiscoveryState;
}

/**
 * Derive which distinct state the list renders. The outage/own-everything split only applies when there is
 * nothing to show — a refresh outage that RETAINS prior rows resolves to `populated` (the tab layers an outage
 * banner over the retained rows), so a transient outage never blanks a populated list to the empty state. When
 * an ok read returns zero rows, the server-decided `state` selects the distinct empty view: a missing key, a
 * missing source id, or a source outage each render their own block rather than the own-everything copy.
 */
export function deriveMissingView(input: MissingViewInput): MissingView {
  if (input.totalRows === 0) {
    if (input.fetchStatus === "loading") return "loading";
    // A thrown fetch (a network error reaching the extension endpoint) is the outage state and takes precedence
    // over any stale discriminator carried on the retained state.
    if (input.fetchStatus === "error") return "outage";
    switch (input.state) {
      case "needsProviderKey":
        return "needsProviderKey";
      case "noSourceId":
        return "noSourceId";
      case "sourceUnreachable":
        return "sourceUnreachable";
      default:
        return "ownEverything";
    }
  }

  if (input.visibleCount > 0) return "populated";
  return "noMatch";
}

/** The human display name for a metadata source, defaulting to Whisparr for an absent/unknown source (an inert legacy default for an older/terminal payload). */
export function sourceLabel(source: MissingDiscoverySource | undefined): string {
  switch (source) {
    case "stashdb":
      return "StashDB";
    case "tpdb":
      return "ThePornDB";
    default:
      return "Whisparr";
  }
}

/**
 * What the connected metadata provider applied over the WHOLE catalogue, per axis. An empty array means the
 * provider applied nothing on that axis and the client is working over the rows it loaded.
 */
export interface DiscoveryCapabilities {
  serverSideSorts: readonly MissingSortMode[];
  serverSideFacets: readonly MissingFacetAxis[];
  wholeSetFacetAxes: readonly MissingFacetAxis[];
}

/**
 * The capabilities a response declares. Every axis defaults to EMPTY — a response that declares nothing supports
 * nothing, which keeps a provider that cannot order from silently appearing to have ordered the whole set, and
 * keeps a page-derived option list from being shown as the whole catalogue. An absent declaration and a
 * declared-empty one mean the same thing, which is why one default covers both.
 */
export function capabilitiesFrom(
  result: Pick<DiscoveryResult, "serverSideSorts" | "serverSideFacets" | "wholeSetFacetAxes">,
): DiscoveryCapabilities {
  return {
    serverSideSorts: result.serverSideSorts ?? [],
    serverSideFacets: result.serverSideFacets ?? [],
    wholeSetFacetAxes: result.wholeSetFacetAxes ?? [],
  };
}

/**
 * Whether the chosen ordering was applied by the provider over the whole catalogue.
 *
 * `asked` is the query the CURRENTLY RENDERED rows were read under, and it is required for a reason: a
 * declared capability says what the provider CAN order and is not evidence that THIS read asked it to.
 * Conflating the two let a restored bookmark render the oldest of the newest page with no notice at all — the
 * provider declared every mode, so the control claimed an ordering it had never performed. An absent `Sort`
 * MEANS the default ordering, so a pristine read carries its own and is not treated as unasked.
 */
export function sortIsServerSide(
  state: MissingFilterState,
  caps: DiscoveryCapabilities,
  asked: DiscoveryQueryFields | undefined,
): boolean {
  const carried = asked?.Sort ?? DEFAULT_SORT_MODE;
  return carried === state.sortMode && caps.serverSideSorts.includes(state.sortMode);
}

/**
 * The line shown beneath a sort control the metadata provider could not honour. It names the PROVIDER as the
 * thing with no ordering to offer, states what the control does instead, and stops there. The limitation is the
 * provider's own and is permanent; Cove is not at fault; and the wording names no remedy because none exists.
 * Switching Whisparr generation is in particular NOT one: the older generation's provider is the only one that
 * filters an exact year natively, which makes the pair near-complements.
 *
 * The sentence is composed here, from the source on THIS response, because the provider it names changes per
 * request — a fixed literal could only name one of the two.
 */
export function providerOrderingNotice(source: MissingDiscoverySource | undefined): string {
  return `${sourceLabel(source)} offers no way to order a whole catalogue; this orders the rows loaded here.`;
}

/** Whether the provider narrows this axis over the whole catalogue when a value is chosen on it. */
export function facetIsServerSide(axis: MissingFacetKey, caps: DiscoveryCapabilities): boolean {
  return caps.serverSideFacets.includes(axis);
}

/**
 * Whether this axis's option list came from an aggregate over the whole catalogue, as against the values the
 * loaded rows carry. Read from the response's own per-read claim — never inferred from how long the list is.
 */
export function facetOptionsAreWholeSet(
  axis: MissingFacetKey,
  caps: DiscoveryCapabilities,
): boolean {
  return caps.wholeSetFacetAxes.includes(axis);
}

/**
 * The axes carrying an ACTIVE selection whose option list is not the whole set. An axis nobody has selected is
 * not degraded — it is merely offering what it has — which is why the selection is part of the test.
 */
export function degradedAxes(
  state: MissingFilterState,
  caps: DiscoveryCapabilities,
): MissingFacetKey[] {
  const axes: MissingFacetKey[] = ["studio", "performer", "tag", "dateYear"];
  return axes.filter((axis) => state[axis] !== null && !facetOptionsAreWholeSet(axis, caps));
}

/**
 * The rendered facet controls whose option list came from the loaded rows.
 *
 * Descriptors, not axis keys: the rendered set already carries the zero-option drop and a parent studio's
 * `Sub-studio` relabel, so a key-built result could name a control that is not on the page, or name one by a
 * word the user cannot see. A whole-set read yields nothing whatever the capabilities declare — the catalogue is
 * in memory, so every option list is complete by construction.
 */
export function pageDerivedFacetAxes(
  facets: readonly MissingFacetDescriptor[],
  caps: DiscoveryCapabilities,
  serverPaged: boolean,
): MissingFacetDescriptor[] {
  if (!serverPaged) return [];
  return facets.filter((facet) => !facetOptionsAreWholeSet(facet.key, caps));
}

function joinLabels(labels: readonly string[]): string {
  const lowered = labels.map((label) => label.toLowerCase());
  if (lowered.length <= 1) return lowered.join("");
  return `${lowered.slice(0, -1).join(", ")} and ${lowered[lowered.length - 1]}`;
}

/**
 * The line shown for the facet controls whose option list came from the loaded rows. It names the metadata
 * PROVIDER as the thing with no aggregate to offer, says the values are the ones seen so far, and then says the
 * thing that stops it reading as a bigger limitation than it is: choosing one still narrows the whole catalogue
 * wherever the provider supports that axis.
 *
 * Those two facts are genuinely separate — on one provider the option LIST is page-derived while the FILTER is
 * exact over everything — and collapsing them into one sentence would be its own small lie. Composed here, from
 * the source on THIS response, because the provider it names changes per request.
 *
 * The axis attribution is what lets one line stand for a toolbar of several controls.
 */
export function providerFacetOptionsNotice(
  source: MissingDiscoverySource | undefined,
  axisLabels: readonly string[],
): string {
  return `${sourceLabel(source)} offers no list of every value for ${joinLabels(axisLabels)} here; these are the ones seen so far. Choosing one still filters the whole catalogue on the axes it supports.`;
}

/**
 * One control's hover text: the axis-local half only. The full sentence is a toolbar property, already on screen
 * once.
 */
export function providerFacetAxisShortNotice(axisLabel: string): string {
  return `${axisLabel}: only the values seen so far are listed.`;
}

/**
 * The own-everything copy: a served result always carries a direct source (StashDB/TPDB), so the copy names the
 * source it actually read rather than making a catalogue claim the read did not come from.
 */
export function ownEverythingCopy(
  name: string,
  source: MissingDiscoverySource | undefined,
): string {
  return `You own every scene ${sourceLabel(source)} lists for ${name} ✓`;
}

/**
 * The no-credential unmonitored copy: a single actionable line pointing at Cove's own metadata-server
 * config, naming the version-correct source (StashDB on v3, ThePornDB on v2). There is no extension-side key
 * field, and the copy is never a misleading empty list.
 */
export function needsCredentialCopy(
  name: string,
  source: MissingDiscoverySource | undefined,
): string {
  const label = sourceLabel(source);
  const version = source === "tpdb" ? "v2" : "v3";
  return `Set up a ${label} (${version}) metadata source in Cove (Settings → Scraping → Metadata servers) to discover ${name}'s unmonitored catalogue.`;
}

/**
 * The page number carried in a host page search string, or 1 when absent, unparseable, or out of range.
 * Page rides the URL contract but deliberately stays OUT of {@link MissingFilterState}: the read query is
 * derived from the filter, and paging must not look like a filter change to that derivation.
 */
export function readPageFromSearch(search: string): number {
  const raw = new URLSearchParams(search).get(PARAM_PAGE);
  const parsed = raw === null ? Number.NaN : Number(raw);
  return Number.isInteger(parsed) && parsed > 1 ? parsed : 1;
}

/**
 * Merge a page number into an existing host search string, returning the new search string (no leading
 * `?`). Page 1 is the default and is REMOVED rather than written, so a first-page view keeps a clean URL
 * and the write→read round-trip is lossless.
 */
export function writePageToSearch(search: string, page: number): string {
  const params = new URLSearchParams(search);
  if (page > 1) {
    params.set(PARAM_PAGE, String(page));
  } else {
    params.delete(PARAM_PAGE);
  }
  return params.toString();
}
