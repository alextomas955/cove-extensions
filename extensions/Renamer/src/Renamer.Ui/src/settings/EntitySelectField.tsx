/**
 * The one Renamer adapter over the host's entity multi-selector. Every selector instance in the
 * settings panel is reached through here, because two of its props must not vary between them.
 *
 * The create affordance stays OFF. The host control otherwise offers an inline "create" row that
 * writes a real entity into the user's library — a surprising write from a screen that only
 * configures rules over the library the user already has, and one the local control it replaces never
 * performed. Locking it here rather than at each instance is the point of this component: a rule
 * enforced at one declaration site is structural, whereas the same rule restated at eight instances is
 * a convention that rots, and omitting it is silent at the instance that forgets.
 *
 * The input class is the shared one so an embedded host control matches every other input in the
 * panel. It is imported, never retyped — the shared export exists so the string has one declaration
 * site. Nothing else about the host control is styled from here.
 *
 * The host input accepts no label, no id and no aria-label, so its accessible name comes only from the
 * wrapping label element — which is why the selector stays inside {@link Field} at every instance.
 *
 * This is an adapter, not a second picker: no state, no searching, no filtering, no results list and
 * no chip rendering live here. All of that is the host's.
 */
import { EntityReferenceMultiSelector, type EntityReferenceType } from "@cove/runtime/components";

import { Field, INPUT_CLASS } from "@cove-extensions/ui-shared";

export function EntitySelectField({
  entityType,
  label,
  helper,
  values,
  onChange,
  placeholder,
  excludeIds,
}: Readonly<{
  entityType: EntityReferenceType;
  /** Empty for an in-row cell, where the surrounding row already identifies the field. */
  label: string;
  helper?: string;
  /** The stored stable ids. Controlled — persistence stays with the panel. */
  values: number[];
  onChange: (values: number[]) => void;
  placeholder?: string;
  /** Ids to keep out of the results, e.g. entities that already key a rule elsewhere. */
  excludeIds?: Iterable<number>;
}>) {
  return (
    <Field label={label} helper={helper}>
      <EntityReferenceMultiSelector
        entityType={entityType}
        values={values}
        onChange={onChange}
        placeholder={placeholder}
        excludeIds={excludeIds}
        allowCreate={false}
        inputClassName={INPUT_CLASS}
      />
    </Field>
  );
}
