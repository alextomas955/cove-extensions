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
    <SectionCard
      title="What gets renamed"
      description="Which items are eligible, and the fields they must have."
    >
      <Toggle
        label="Only rename organized items"
        checked={options.OnlyOrganized}
        onChange={(v) => {
          set("OnlyOrganized", v);
        }}
        helper="Only touches items you've marked Organized — un-curated files are skipped."
      />
      <Toggle
        label="Use filename as title when none is set"
        checked={options.FilenameAsTitle}
        onChange={(v) => {
          set("FilenameAsTitle", v);
        }}
        helper="When an item has no title, use its current filename (without extension) as the title."
      />
      <Field
        label="Required fields"
        helper="Items whose listed tokens resolve to nothing are skipped instead of renamed. Default: title."
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
