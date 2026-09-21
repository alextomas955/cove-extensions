/**
 * What a facet menu learns by asking the metadata source about a typed fragment.
 *
 * Kept apart from the catalogue layer so that drawing a menu does not pull the host's
 * authenticated transport in behind it. The menu is handed the way to ask.
 */
import { useEffect, useState } from "react";

import type { MissingFacetSearchView } from "../wire/api";
import {
  facetLookupIn,
  MINIMUM_FACET_FRAGMENT,
  type MissingFacetLookup,
} from "./missingFacetLogic";
import { searchSettleDelayMs } from "./missingToolbarLogic";

export type FacetValueSearch = (
  facetKey: string,
  fragment: string,
) => Promise<MissingFacetSearchView>;

/**
 * What the values of `facetKey` matching `fragment` currently answer.
 *
 * Typing settles before anything is sent, so a typed word costs one request. An answer is held
 * under the fragment it was asked about and read back only under that fragment, so two requests
 * in flight may settle in either order without the older one replacing the newer answer.
 *
 * @param search how to ask, or undefined where nothing can be asked
 * @param facetKey the facet to ask about, or undefined for a menu that is not a facet
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
