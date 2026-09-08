/**
 * The status badge on a studio card and on a performer card.
 *
 * The host spreads its slot context as top-level props, so each exported component's props are
 * exactly what that context carries. A read through a nested context object throws into the card's
 * error boundary. Only the Cove id is declared and only the Cove id is read: the identifier the
 * instance is given is re-resolved on the server from the library's own identity row.
 */
import { CardStatusBadge } from "./CardStatusBadge";

export function WhisparrStudioCardBadge({ studio }: { studio: { id: number } }) {
  return <CardStatusBadge kind="studio" coveId={studio.id} />;
}

export function WhisparrPerformerCardBadge({ performer }: { performer: { id: number } }) {
  return <CardStatusBadge kind="performer" coveId={performer.id} />;
}
