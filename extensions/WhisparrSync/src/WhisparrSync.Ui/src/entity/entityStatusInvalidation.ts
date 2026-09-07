/**
 * Store-facing composition that re-reads both studio/performer status surfaces from Whisparr after a
 * status-changing bulk action: the per-card badges (entityCardStatusStore) and the toolbar-row count
 * (entityLibrarySummaryStore). It holds no decision logic — whether an op mutates status lives in the
 * pure entitiesBatchLogic (opMutatesEntityStatus); this module only wires the two refetches together.
 */
import type { EntityBatchKind } from "./entitiesBatchLogic";
import { refreshEntityCardStatus } from "./entityCardStatusStore";
import { refreshEntityLibrarySummary } from "./entityLibrarySummaryStore";

/** Evict + refetch the selected entities' card status and their kind's toolbar summary so both badge surfaces re-read Whisparr. */
export function invalidateEntityStatus(kind: EntityBatchKind, coveEntityIds: number[]): void {
  refreshEntityCardStatus(kind, coveEntityIds);
  refreshEntityLibrarySummary(kind);
}
