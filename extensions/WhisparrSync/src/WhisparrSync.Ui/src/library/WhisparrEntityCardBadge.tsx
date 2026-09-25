/**
 * The status badge on a studio card and on a performer card.
 *
 * The host spreads its slot context as top-level props, so a read through a nested context object
 * throws into the card's error boundary. Only the Cove id is read; the identifier Whisparr is given
 * is re-resolved on the server.
 */
import { CardStatusBadge } from "./CardStatusBadge";

export function WhisparrStudioCardBadge({ studio }: Readonly<{ studio: { id: number } }>) {
  return <CardStatusBadge kind="studio" coveId={studio.id} />;
}

export function WhisparrPerformerCardBadge({ performer }: Readonly<{ performer: { id: number } }>) {
  return <CardStatusBadge kind="performer" coveId={performer.id} />;
}
