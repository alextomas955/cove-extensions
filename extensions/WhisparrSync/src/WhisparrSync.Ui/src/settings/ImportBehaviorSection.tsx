/**
 * What a redelivery naming a different file does to the scene Cove already holds.
 *
 * Neither choice moves, renames or deletes a file on disk, so both consequence sentences say so.
 */
import { Field, SectionCard, Select, StatusText } from "@cove-extensions/ui-shared";

import type { UpgradeBehavior } from "../wire/api";
import {
  UPGRADE_DESCRIPTION,
  UPGRADE_DROPS_THE_SUPERSEDED_FILE,
  UPGRADE_FIELD,
  UPGRADE_KEEP_BOTH,
  UPGRADE_KEEP_ONLY_THE_NEW_FILE,
  UPGRADE_KEEPS_BOTH_FILES,
  UPGRADE_TITLE,
} from "../common/ui/copy";
import { OFF_SCREEN } from "../common/ui/offScreen";

export interface ImportBehaviorSectionProps {
  /** Null until the stored value has arrived, which is not a choice anyone made. */
  behavior: UpgradeBehavior | null;
  /** The one reason several controls on this page share, stated once by the page's own notice. */
  sharedReason: string | null;
  onChange: (next: UpgradeBehavior) => void;
}

const CHOICES: readonly { value: UpgradeBehavior; label: string; consequence: string }[] = [
  { value: "add", label: UPGRADE_KEEP_BOTH, consequence: UPGRADE_KEEPS_BOTH_FILES },
  {
    value: "replace",
    label: UPGRADE_KEEP_ONLY_THE_NEW_FILE,
    consequence: UPGRADE_DROPS_THE_SUPERSEDED_FILE,
  },
];

export function ImportBehaviorSection({
  behavior,
  sharedReason,
  onChange,
}: Readonly<ImportBehaviorSectionProps>) {
  const chosen = CHOICES.find((choice) => choice.value === behavior) ?? null;

  return (
    <SectionCard title={UPGRADE_TITLE} description={UPGRADE_DESCRIPTION}>
      <div className="space-y-2" title={sharedReason ?? undefined}>
        {/* The reason follows the label inside it, so the control is announced by its own name and
            then by why it cannot be used. It is off-screen because the page states it once. */}
        <Field label={UPGRADE_FIELD} labelStyle="mono">
          {(id) => (
            <>
              <Select
                id={id}
                value={behavior ?? "add"}
                options={CHOICES.map((choice) => ({ value: choice.value, label: choice.label }))}
                disabled={behavior === null || sharedReason !== null}
                onChange={onChange}
              />
              {sharedReason === null ? null : <span style={OFF_SCREEN}>{sharedReason}</span>}
            </>
          )}
        </Field>

        {chosen === null ? null : <StatusText kind="muted">{chosen.consequence}</StatusText>}
      </div>
    </SectionCard>
  );
}
