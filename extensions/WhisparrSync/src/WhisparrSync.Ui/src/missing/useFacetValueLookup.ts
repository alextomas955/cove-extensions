/**
 * What a facet menu learns by asking the metadata source about a typed fragment.
 *
 * Kept apart from the catalogue layer so that drawing a menu does not pull the host's authenticated
 * transport in behind it: the menu is handed the way to ask rather than reaching for one.
 */
import { useEffect, useState } from "react";

import type { MissingFacetSearchView } from "../wire/api";
import {
  facetLookupIn,
  MINIMUM_FACET_FRAGMENT,
  type MissingFacetLookup,
} from "./missingFacetLogic";
import { searchSettleDelayMs } from "./missingToolbarLogic";

/** Asks the metadata source which values of one facet match a fragment. */
export type FacetValueSearch = (
  facetKey: string,
  fragment: string,
) => Promise<MissingFacetSearchView>;

/**
 * What the values of `facetKey` matching `fragment` currently answer.
 *
 * Typing settles before anything is sent, on the delay the title search already settles on, so a
 * typed word costs one request rather than one per key press.
 *
 * An answer is held under the fragment it was asked about and read back only under that same
 * fragment, so an answer is never drawn beneath a fragment it does not answer. A request the reader
 * has typed past is dropped as they type past it, so two in flight together settle in whatever order
 * they settle in and the older one neither replaces the newer answer nor erases it.
 *
 * @param search how to ask, or undefined where nothing can be asked
 * @param facetKey the facet to ask about, or undefined for a menu that is not a facet
 * @param fragment what the reader typed
 */
export function useFacetValueLookup(
  search: FacetValueSearch | undefined,
  facetKey: string | undefined,
  fragment: string,
): MissingFacetLookup {
  const [answered, setAnswered] = useState<{
    fragment: string;
    lookup: MissingFacetLookup;
  } | null>(null);

  const wanted = fragment.trim();

  useEffect(() => {
    if (search === undefined || facetKey === undefined || wanted.length < MINIMUM_FACET_FRAGMENT) {
      return undefined;
    }

    let live = true;
    const timer = setTimeout(() => {
      search(facetKey, wanted)
        .then((answer) => {
          if (live) setAnswered({ fragment: wanted, lookup: facetLookupIn(answer) });
        })
        .catch(() => {
          if (live) setAnswered({ fragment: wanted, lookup: { state: "notRead" } });
        });
    }, searchSettleDelayMs);

    return () => {
      live = false;
      clearTimeout(timer);
    };
  }, [search, facetKey, wanted]);

  if (search === undefined || facetKey === undefined || wanted.length < MINIMUM_FACET_FRAGMENT) {
    return { state: "handed" };
  }

  return answered?.fragment === wanted ? answered.lookup : { state: "asking" };
}
