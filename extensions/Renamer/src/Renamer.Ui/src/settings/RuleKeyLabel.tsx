/**
 * The label for a committed routing-rule key: the entity's name, or a plain statement that the entity
 * is gone.
 *
 * The host's `EntityReferenceValue` resolves an id to a name and falls back to "Loading <kind>…" when
 * it cannot — permanently, for an id nothing answers to, because its own "Unavailable" branch is
 * unreachable while that fallback text is non-empty. A rule whose studio was merged away therefore
 * reads as a stuck spinner rather than as a rule that no longer applies.
 *
 * Which ids are gone is decided by the server, not here: absence cannot be told apart in the browser
 * from an entity this viewer may not read, or from a request that failed.
 */
import { EntityReferenceValue } from "@cove/runtime/components";

import type { EntityReferenceType } from "@cove/runtime/components";

import { orphanedRuleLabel } from "./ruleKeyLabelLogic";

/** What a rule key is labelled with, and whether the entity behind it still exists. */
export function RuleKeyLabel({
  entityType,
  id,
  orphaned,
}: {
  entityType: EntityReferenceType;
  id: number;
  /** True when the server reported this id as naming no entity. */
  orphaned: boolean;
}) {
  if (!orphaned) {
    return <EntityReferenceValue entityType={entityType} value={id} />;
  }

  return <span className="text-muted-foreground">{orphanedRuleLabel(entityType, id)}</span>;
}
