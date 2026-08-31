/**
 * What a redelivery naming a different file does to the scene you already have.
 *
 * Presentational. Every value arrives as a prop and no request is issued here.
 *
 * Neither choice moves, renames or deletes a file in either system's storage, so both sentences say
 * what happens on disk rather than leaving one of them to be read as the exception.
 */
import { Field, SectionCard, Select, Spinner, StatusText } from "@cove-extensions/ui-shared";

import type { UpgradeBehavior } from "../wire/api";
import { UPGRADE_DROPS_THE_SUPERSEDED_FILE, UPGRADE_KEEPS_BOTH_FILES } from "../common/ui/copy";

export interface ImportBehaviorSectionProps {
  /** The stored behaviour, or null before the settings read answers. */
  behavior: UpgradeBehavior | null;
  saving: boolean;
  saveError: string | null;
  /** The one reason several controls on this page share, stated once by the page's own notice. */
  sharedReason: string | null;
  onChange: (next: UpgradeBehavior) => void;
}

/** The consequence of each choice, in the order the control offers them. */
const CHOICES: readonly { value: UpgradeBehavior; label: string; consequence: string }[] = [
  { value: "add", label: "Keep both files", consequence: UPGRADE_KEEPS_BOTH_FILES },
  {
    value: "replace",
    label: "Keep only the new file",
    consequence: UPGRADE_DROPS_THE_SUPERSEDED_FILE,
  },
];

export function ImportBehaviorSection({
  behavior,
  saving,
  saveError,
  sharedReason,
  onChange,
}: ImportBehaviorSectionProps) {
  const chosen = CHOICES.find((choice) => choice.value === behavior) ?? null;

  return (
    <SectionCard
      title="When Whisparr replaces a file"
      description="What happens to the scene Cove already holds when a better file arrives for it."
    >
      <div className="space-y-2" aria-busy={saving}>
        <Field label="Replacement files">
          <Select
            value={behavior ?? "add"}
            options={CHOICES.map((choice) => ({ value: choice.value, label: choice.label }))}
            disabled={behavior === null || saving || sharedReason !== null}
            onChange={onChange}
          />
        </Field>

        {chosen === null ? null : <StatusText kind="muted">{chosen.consequence}</StatusText>}

        {saving ? <Spinner /> : null}

        {saveError === null ? null : (
          <StatusText kind="error">Cove could not save that choice: {saveError}</StatusText>
        )}
      </div>
    </SectionCard>
  );
}
