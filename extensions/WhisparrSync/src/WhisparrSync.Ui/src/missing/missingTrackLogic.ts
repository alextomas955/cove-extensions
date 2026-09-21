/** What each answer to the add that tracks an entity's catalogue says to a reader. */
import {
  ADD_TO_WHISPARR_DID_NOT_TAKE,
  NO_INSTANCE_CONNECTED,
  NO_PROVIDER_ID_FOR_ENTITY,
  WHISPARR_CANNOT_TRACK_THIS_KIND,
  WHISPARR_HOLDS_NO_ADD_DEFAULTS,
} from "../common/ui/copy";
import type { MissingTrackOutcome } from "../wire/api";

/**
 * The sentence for `outcome`, with its name slots still to fill.
 *
 * Total by type, so an outcome added to the wire enum fails this build.
 */
export function trackRefusal(outcome: MissingTrackOutcome): string {
  const sentences: Record<MissingTrackOutcome, string> = {
    added: "",
    noInstanceConnected: NO_INSTANCE_CONNECTED,
    generationCannotTrackThisKind: WHISPARR_CANNOT_TRACK_THIS_KIND,
    noIdentifier: NO_PROVIDER_ID_FOR_ENTITY,
    noAddDefaults: WHISPARR_HOLDS_NO_ADD_DEFAULTS,
    refused: ADD_TO_WHISPARR_DID_NOT_TAKE,
    notStarted: ADD_TO_WHISPARR_DID_NOT_TAKE,
  };

  return sentences[outcome];
}
