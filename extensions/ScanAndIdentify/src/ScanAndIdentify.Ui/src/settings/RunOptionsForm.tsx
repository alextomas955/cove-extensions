import { Button, SectionCard, Spinner, StatusText, Toggle } from "@cove-extensions/ui-shared";

import { RUN_OPTION_FIELDS } from "./runOptions";
import { useRunOptions } from "./useRunOptions";

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
          <div className="space-y-2">
            {RUN_OPTION_FIELDS.map((field) => (
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
