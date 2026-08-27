/**
 * WhatGetsRenamedSection — the "What gets renamed" card: eligibility toggles (organized-only,
 * filename-as-title) and the required-fields token list. Presentational; every edit flows up through
 * the `set` callback the panel threads in from useRenamerOptions.
 */
import { type RenamerOptions } from "./options";
import { Field, Toggle, TagListInput, SectionCard, TokenPicker } from "@cove-extensions/ui-shared";
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
        checked={options.OnlyOrganized}
        onChange={(v) => {
          set("OnlyOrganized", v);
        }}
      />
      <Toggle
        label="Use filename as title when none is set"
        checked={options.FilenameAsTitle}
        onChange={(v) => {
          set("FilenameAsTitle", v);
        }}
        helper="The filename without its extension, saved onto the item so later renames read the stored title."
      />
      <Field
        label="Required fields"
        helper="An item whose listed tokens resolve to nothing is skipped."
      >
        <TagListInput
          values={options.RequiredFields}
          onChange={(v) => {
            set("RequiredFields", v);
          }}
          placeholder="Add token, press Enter"
        />
        <TokenPicker
          tokens={BARE_TOKENS}
          values={options.RequiredFields}
          onAdd={(name) => {
            set(
              "RequiredFields",
              options.RequiredFields.includes(name)
                ? options.RequiredFields
                : [...options.RequiredFields, name],
            );
          }}
        />
        <TokenAdvisory values={options.RequiredFields} />
      </Field>
    </SectionCard>
  );
}
