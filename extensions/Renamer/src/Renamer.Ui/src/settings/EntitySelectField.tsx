/**
 * The one adapter over the host's entity multi-selector. Every selector instance in the settings
 * panel is reached through here, because two of its props must not vary between them.
 *
 * The create affordance stays off. The host control otherwise offers an inline "create" row that
 * writes a real entity into the user's library, a surprising write from a screen that only
 * configures rules over the library the user already has. Locking it at one declaration site is what
 * this component is for: omitting the prop is silent at the instance that forgets.
 *
 * The input class is the shared one so an embedded host control matches every other input in the
 * panel. It is imported, never retyped.
 *
 * The wrapper is deliberately not a label element. The host draws each chip's Remove button ahead of
 * its input, so a label around it names that button and a click on the heading removes a chip. The
 * block carries the name instead, and the input is handed the same string through the host's own
 * `inputAriaLabel`. There is no input id to point `htmlFor` at, and a host predating `inputAriaLabel`
 * drops the prop, leaving that input on its placeholder.
 *
 * No state, no searching, no filtering, no results list and no chip rendering live here. All of that
 * is the host's.
 */
import { EntityReferenceMultiSelector, type EntityReferenceType } from "@cove/runtime/components";

import { Field, INPUT_CLASS } from "@cove-extensions/ui-shared";

type SelectorProps = Parameters<typeof EntityReferenceMultiSelector>[0];

export function EntitySelectField({
  entityType,
  label,
  labelStyle,
  helper,
  values,
  onChange,
  placeholder,
  excludeIds,
}: Readonly<{
  entityType: EntityReferenceType;
  label: string;
  labelStyle?: "micro" | "group";
  helper?: string;
  /** The stored stable ids. Controlled: persistence stays with the panel. */
  values: number[];
  onChange: (values: number[]) => void;
  placeholder?: string;
  /** Ids to keep out of the results, e.g. entities that already key a rule elsewhere. */
  excludeIds?: Iterable<number>;
}>) {
  const declared: SelectorProps = {
    entityType,
    values,
    onChange,
    placeholder,
    excludeIds,
    allowCreate: false,
    inputClassName: INPUT_CLASS,
  };

  // The host gained `inputAriaLabel` after the 1.4.1 floor this extension declares, so it is absent
  // from the shared declaration and reaches the control through this one widening. A host predating
  // it ignores the unknown prop.
  const withName = {
    ...declared,
    inputAriaLabel: label,
  } as SelectorProps & { inputAriaLabel: string };

  return (
    <Field label={label} labelStyle={labelStyle} helper={helper} controlNamesItself>
      <EntityReferenceMultiSelector {...withName} />
    </Field>
  );
}
