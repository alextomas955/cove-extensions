/**
 * Announced when what this page's sections read under the stored connection is no longer what they
 * hold.
 *
 * Every section reads once when it mounts, and several of them read something the connection
 * decides: whether an instance can be asked at all, what its callback registration says, what a
 * library sync would cover. A save that only changed the address or the key left all of them
 * holding the answer from before there was a connection, and a test of the stored connection is the
 * one call that re-reads the registration off the instance.
 *
 * The announcement carries nothing. What each section now shows is its own to read, because each
 * reads a different route and none of them can be answered from a settings save.
 */
type Listener = () => void;

const listeners = new Set<Listener>();

/** Subscribes to every announcement, and returns the unsubscribe. */
export function onConnectionChanged(listener: Listener): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** States that the stored connection is no longer the one the page's sections read under. */
export function announceConnectionChanged(): void {
  for (const listener of [...listeners]) listener();
}
