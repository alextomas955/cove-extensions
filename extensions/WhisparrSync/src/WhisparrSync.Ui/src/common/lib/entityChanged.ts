/**
 * Announced when something this browser did changed what the instance holds for one entity.
 *
 * A studio or performer page draws two surfaces over the same entity: the control in its action
 * row, and the tab listing what the reader does not own. Each acts on the instance, and each acts
 * on what the other shows. Neither slice imports the other, which is the rule for two feature
 * slices, and the announcement carries no answer of its own: what the instance now holds is each
 * listener's to read.
 */
import type { WhisparrEntityKind } from "../../wire/api";

/** The entity an announcement is about. */
export interface ChangedEntity {
  readonly kind: WhisparrEntityKind;
  readonly coveId: number;
}

type Listener = (entity: ChangedEntity) => void;

const listeners = new Set<Listener>();

/** Subscribes to every announcement, and returns the unsubscribe. */
export function onEntityChanged(listener: Listener): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** States that what the instance holds for `entity` is no longer what was last read. */
export function announceEntityChanged(entity: ChangedEntity): void {
  for (const listener of [...listeners]) listener(entity);
}

/** Whether an announcement is about the entity a listener is showing. */
export function isTheSameEntity(announced: ChangedEntity, shown: ChangedEntity): boolean {
  return announced.kind === shown.kind && announced.coveId === shown.coveId;
}
