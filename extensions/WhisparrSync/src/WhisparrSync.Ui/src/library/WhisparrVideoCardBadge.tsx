/**
 * The status badge on a scene card.
 *
 * The host spreads its slot context as top-level props, so this component's props are exactly what
 * that context carries. Only the Cove id is declared and only the Cove id is read: the host object
 * also carries the library's own identity rows, and which of them names the scene is re-resolved on
 * the server, so a browser reading one would be naming the entity a third party is asked about.
 */
import { CardStatusBadge } from "./CardStatusBadge";

export function WhisparrVideoCardBadge({ video }: { video: { id: number } }) {
  return <CardStatusBadge kind="video" coveId={video.id} />;
}
