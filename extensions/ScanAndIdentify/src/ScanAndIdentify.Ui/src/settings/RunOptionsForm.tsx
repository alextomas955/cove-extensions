import {
  Button,
  FieldGroup,
  SectionCard,
  Spinner,
  StatusText,
  Toggle,
} from "@cove-extensions/ui-shared";

import { RUN_OPTION_FIELDS, RUN_OPTION_GROUPS, type RunOptionGroup } from "./runOptions";
import { useRunOptions } from "./useRunOptions";

// The question the missing switch raises. Nothing a run does replaces an asset that already exists,
// so the answer belongs beside the switches rather than only in the documentation.
const GROUP_NOTES: Partial<Record<RunOptionGroup, string>> = {
  "Images and previews":
    "A run never replaces an asset that already exists, so running it again only fills the gaps.",
};

/** The saved description of a run: what a later run performs, and how. */
export function RunOptionsForm() {
  const { options, loading, loadError, saving, saveError, canSave, onSave, discard, set } =
    useRunOptions();

  return (
    <SectionCard
      title="Run options"
      description="What a run does. Nothing runs yet; these are saved for the run to read."
    >
      {loading ? (
        <div className="flex items-center gap-2 text-sm text-secondary">
          <Spinner />
          Reading the saved options…
        </div>
      ) : null}

      {loadError === null ? null : (
        <StatusText kind="error">Couldn&apos;t read the saved options: {loadError}</StatusText>
      )}

      {options === null ? null : (
        <div className="space-y-4">
          {RUN_OPTION_GROUPS.map((group) => (
            <FieldGroup key={group} label={group} labelStyle="group">
              {GROUP_NOTES[group] === undefined ? null : (
                <p className="mb-2 text-xs text-secondary">{GROUP_NOTES[group]}</p>
              )}
              <div className="space-y-2">
                {RUN_OPTION_FIELDS.filter((field) => field.group === group).map((field) => (
                  <Toggle
                    key={field.key}
                    label={field.label}
                    helper={field.helper}
                    checked={options[field.key]}
                    onChange={(checked) => {
                      set(field.key, checked);
                    }}
                  />
                ))}
              </div>
            </FieldGroup>
          ))}

          <div className="flex items-center gap-2">
            <Button
              onClick={() => {
                void onSave();
              }}
              disabled={!canSave}
            >
              {saving ? "Saving…" : "Save"}
            </Button>
            <Button variant="ghost" onClick={discard} disabled={!canSave}>
              Discard
            </Button>
            {saveError === null ? null : (
              <StatusText kind="error">Couldn&apos;t save: {saveError}</StatusText>
            )}
          </div>
        </div>
      )}
    </SectionCard>
  );
}
