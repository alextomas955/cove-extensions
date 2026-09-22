/**
 * WhatGetsRenamedSection — the "What gets renamed" card: eligibility toggles (organized-only,
 * filename-as-title) and the required-fields token list. Presentational; every edit flows up through
 * the `set` callback the panel threads in from useRenamerOptions.
 */
import { type RenamerOptions } from "./options";
import { Toggle, TagListInput, SectionCard } from "@cove-extensions/ui-shared";
import { BARE_TOKENS } from "./templateValidation";
import { TokenAdvisory } from "./templateAdvisories";

/**
 * The required-fields token list, headed and explained. The wrapper is a div and not a label: a
 * label forwards a click inside it to its first labelable descendant, which here is a chip's Remove
 * button, so picking a suggestion would delete a chip. With no label element the input takes its
 * name from `ariaLabel` instead.
 */
function RequiredFields({
  values,
  onChange,
}: {
  values: string[];
  onChange: (values: string[]) => void;
}) {
  return (
    <div>
      <span className="block text-sm text-secondary">Required fields</span>
      <p className="mt-1 text-xs text-secondary">An item missing any of these is skipped.</p>
      <div className="mt-2 space-y-2">
        <TagListInput
          values={values}
          onChange={onChange}
          suggestions={BARE_TOKENS}
          placeholder="Add a token — type to search"
          ariaLabel="Required fields"
        />
        <TokenAdvisory values={values} />
      </div>
    </div>
  );
}

export interface WhatGetsRenamedSectionProps {
  options: RenamerOptions;
  set: <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => void;
}

export function WhatGetsRenamedSection({ options, set }: WhatGetsRenamedSectionProps) {
  return (
    <SectionCard title="What gets renamed">
      <Toggle
        label="Only rename organized items"
        helper="Skips anything not marked organized, unless an unorganized destination is set."
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
      <RequiredFields
        values={options.requiredFields}
        onChange={(v) => {
          set("requiredFields", v);
        }}
      />
    </SectionCard>
  );
}
