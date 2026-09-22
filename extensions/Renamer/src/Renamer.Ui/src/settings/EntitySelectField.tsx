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
 * The wrapper is a `FieldGroup`, not a label element. The host draws each chip's Remove button ahead
 * of its input, so a label around it would name that button and a click on the heading would remove
 * a chip. The block carries the name instead.
 *
 * The input the host draws therefore carries no accessible name of its own: the selector exposes
 * neither an id to point `htmlFor` at nor a name hook on the Cove floor this extension declares, and
 * a group's name does not reach a textbox nested inside it. That gap is recorded twice — here, and
 * executably as the named allowance in `settingsFieldNaming.test.ts`, which fails when it matches
 * nothing. A Cove release exposing a name hook on the selector closes it: pass the label through,
 * then delete the allowance, which will by then be failing.
 *
 * No state, no searching, no filtering, no results list and no chip rendering live here. All of that
 * is the host's.
 */
import { EntityReferenceMultiSelector, type EntityReferenceType } from "@cove/runtime/components";

import { FieldGroup, INPUT_CLASS } from "@cove-extensions/ui-shared";

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

  return (
    <FieldGroup label={label} labelStyle={labelStyle} helper={helper}>
      <EntityReferenceMultiSelector {...declared} />
    </FieldGroup>
  );
}
