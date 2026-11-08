import type { SetOption } from "./useRenamerOptions";
import { type RenamerOptions } from "./options";
import { Toggle, TagListInput, SectionCard } from "@cove-extensions/ui-shared";
import { BARE_TOKENS } from "./templateLogic";
import { TokenAdvisory } from "./templateAdvisories";

/**
 * The heading is a plain element, not a label: a label forwards a click inside it to its first
 * labelable descendant, which for a chip list is a chip's Remove button, so a click on the heading
 * would delete a token. `FieldGroup` is the shared primitive for that, but its DOM and classes
 * differ from this block's, and the e2e field selector reads this DOM. With no label element the
 * input takes its name from `ariaLabel`.
 */
function RequiredFields({
  values,
  onChange,
}: Readonly<{
  values: string[];
  onChange: (values: string[]) => void;
}>) {
  return (
    <div>
      <span className="block text-sm text-secondary">Required fields</span>
      <p className="mt-1 text-xs text-secondary">An item missing any of these is skipped.</p>
      <div className="mt-2 space-y-2">
        <TagListInput
          values={values}
          onChange={onChange}
          suggestions={BARE_TOKENS}
          placeholder="Type to add a token"
          ariaLabel="Required fields"
        />
        <TokenAdvisory values={values} />
      </div>
    </div>
  );
}

interface WhatGetsRenamedSectionProps {
  options: RenamerOptions;
  set: SetOption;
}

export function WhatGetsRenamedSection({ options, set }: Readonly<WhatGetsRenamedSectionProps>) {
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
