/**
 * WhatGetsRenamedSection — the "What gets renamed" card: eligibility toggles (organized-only,
 * filename-as-title) and the required-fields token list. Presentational; every edit flows up through
 * the `set` callback the panel threads in from useRenamerOptions.
 */
import { type RenamerOptions } from "./options";
import { Field, Toggle, TagListInput, SectionCard } from "@cove-extensions/ui-shared";
import { BARE_TOKENS } from "./templateValidation";
import { TokenAdvisory } from "./templateAdvisories";

export interface WhatGetsRenamedSectionProps {
  options: RenamerOptions;
  set: <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => void;
}

export function WhatGetsRenamedSection({ options, set }: WhatGetsRenamedSectionProps) {
  return (
    <SectionCard title="What gets renamed">
      <Toggle
        label="Only rename organized items"
        checked={options.onlyOrganized}
        onChange={(v) => {
          set("onlyOrganized", v);
        }}
      />
      <Toggle
        label="Use filename as title when none is set"
        checked={options.filenameAsTitle}
        onChange={(v) => {
          set("filenameAsTitle", v);
        }}
        helper="The filename without its extension, saved onto the item so later renames read the stored title."
      />
      <Field
        label="Required fields"
        helper="An item whose listed tokens resolve to nothing is skipped."
      >
        <TagListInput
          values={options.requiredFields}
          onChange={(v) => {
            set("requiredFields", v);
          }}
          suggestions={BARE_TOKENS}
          placeholder="Add a token — type to search"
        />
        <TokenAdvisory values={options.requiredFields} />
      </Field>
    </SectionCard>
  );
}
