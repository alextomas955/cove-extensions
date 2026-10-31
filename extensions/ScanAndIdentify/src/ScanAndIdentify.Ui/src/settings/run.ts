/**
 * The settings panel's vocabulary for a run.
 *
 * The run is the generated wire contract. The backend owns the run record, what each stage state
 * means and when a run ends; this panel reads what `GET /run` hands it and reports what it started.
 * Per-member documentation stays on the C# contract.
 */

import type * as Wire from "../wire/api";
import type { Complete } from "../common/lib/wire";

export type RunView = Complete<Wire.RunView>;
export type RunStageView = Complete<Wire.RunStageView>;
export type RunStage = Wire.RunStage;
export type RunStageState = Wire.RunStageState;

/** Why a run stopped. The wire type admits null, which is the in-flight case rather than a reason. */
export type RunEndReason = NonNullable<Wire.RunEndReason>;
