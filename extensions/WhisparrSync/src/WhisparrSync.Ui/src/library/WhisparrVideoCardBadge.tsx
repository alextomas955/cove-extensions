/**
 * The status badge on a scene card.
 *
 * The host spreads its slot context as top-level props. Only the Cove id is read; the identity rows
 * the host object also carries are re-resolved on the server.
 */
import { CardStatusBadge } from "./CardStatusBadge";

export function WhisparrVideoCardBadge({ video }: Readonly<{ video: { id: number } }>) {
  return <CardStatusBadge kind="video" coveId={video.id} />;
}
