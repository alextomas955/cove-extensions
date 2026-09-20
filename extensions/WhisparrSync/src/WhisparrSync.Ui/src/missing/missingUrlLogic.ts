/**
 * What the catalogue tab keeps in the page URL, and how it is read and written.
 *
 * The subscription that reads and writes the address bar lives beside this module.
 */

// Prefixed because the host's own tab switch deletes every key in its managed list on every tab
// change, including the change into this tab, so a plain name is erased on arrival.
export const MISSING_URL_KEYS = {
  q: "wsmQ",
  page: "wsmPage",
  sort: "wsmSort",
  filters: "wsmFilters",
} as const;

/** What a shared link to this tab carries. */
export interface MissingView {
  /** The title search, or an empty string for none. */
  readonly q: string;
  /** Counted from one. */
  readonly page: number;
  /**
   * One opaque ordering value the provider issued, or null for the provider's own order.
   *
   * Never an ordering plus a direction: one provider carries the direction inside each value and
   * the other carries it separately, so a direction model of this product's own would be wrong for
   * one of them.
   */
  readonly sort: string | null;
  /** Keyed by the facet keys the provider issued. */
  readonly filters: Readonly<Record<string, string>>;
}

/** The view a URL carrying none of this tab's keys describes. */
export const DEFAULT_MISSING_VIEW: MissingView = { q: "", page: 1, sort: null, filters: {} };

/** What `search` says this tab is looking at. */
export function readMissingView(search: string): MissingView {
  const params = new URLSearchParams(search);
  return {
    q: params.get(MISSING_URL_KEYS.q) ?? DEFAULT_MISSING_VIEW.q,
    page: readPage(params.get(MISSING_URL_KEYS.page)),
    sort: emptyToNull(params.get(MISSING_URL_KEYS.sort)),
    filters: readFilters(params.get(MISSING_URL_KEYS.filters)),
  };
}

/**
 * `search` rewritten to say that this tab is looking at `view`.
 *
 * A key at its default is absent rather than present and blank, and a key this tab does not own is
 * carried through untouched.
 */
export function writeMissingView(search: string, view: MissingView): string {
  const params = new URLSearchParams(search);
  set(params, MISSING_URL_KEYS.q, view.q === "" ? null : view.q);
  set(params, MISSING_URL_KEYS.page, view.page <= 1 ? null : String(view.page));
  set(params, MISSING_URL_KEYS.sort, view.sort);
  set(params, MISSING_URL_KEYS.filters, writeFilters(view.filters));
  return params.toString();
}

function set(params: URLSearchParams, key: string, value: string | null): void {
  if (value === null || value === "") {
    params.delete(key);
    return;
  }
  params.set(key, value);
}

// A value that is not a whole number above zero reads as the first page rather than as a failure.
// A shared link is edited by hand, and a broken one should show the catalogue.
function readPage(raw: string | null): number {
  const parsed = Number(raw);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : DEFAULT_MISSING_VIEW.page;
}

function emptyToNull(raw: string | null): string | null {
  return raw === null || raw === "" ? null : raw;
}

// Each pair is its own encoded segment, so a provider value carrying the separators survives the
// round trip.
function readFilters(raw: string | null): Readonly<Record<string, string>> {
  if (raw === null || raw === "") {
    return {};
  }

  const filters: Record<string, string> = {};
  for (const pair of raw.split(",")) {
    const at = pair.indexOf(":");
    if (at <= 0) {
      continue;
    }
    const key = decodeURIComponent(pair.slice(0, at));
    const value = decodeURIComponent(pair.slice(at + 1));
    if (key !== "" && value !== "") {
      filters[key] = value;
    }
  }
  return filters;
}

function writeFilters(filters: Readonly<Record<string, string>>): string | null {
  // Sorted, so one selection produces one address however the object was built up.
  const pairs = Object.entries(filters)
    .filter(([key, value]) => key !== "" && value !== "")
    .sort(([left], [right]) => (left < right ? -1 : left > right ? 1 : 0))
    .map(([key, value]) => `${encodeURIComponent(key)}:${encodeURIComponent(value)}`);
  return pairs.length === 0 ? null : pairs.join(",");
}
