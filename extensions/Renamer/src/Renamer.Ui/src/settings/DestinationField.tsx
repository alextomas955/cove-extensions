/**
 * DestinationField - the one editor every destination in this panel uses: a ROOT chosen from Cove's
 * library paths, plus a relative folder template rendered under it.
 *
 * No path is ever typed. Cove owns the library paths, so a typed copy of one here would point at
 * nothing the moment the user changed it in Cove; a chosen root is a reference that follows.
 *
 * Presentational - the paths arrive from {@link useLibraryPaths} and every edit flows up through
 * `onChange`.
 */
import type { Ref } from "react";

import { Field, Select, TextInput, PathShapeHint, StatusText } from "@cove-extensions/ui-shared";

import {
  CONTAINING_ROOT,
  CONTAINING_ROOT_LABEL,
  destinationPicker,
  type Destination,
  type LibraryPathsState,
} from "./options";

export interface DestinationFieldProps {
  value: Destination;
  onChange: (value: Destination) => void;
  /** Cove's configured library paths, from `/library-paths`, as the panel currently knows them. */
  library: LibraryPathsState;
  /** Shown above the template input; omit inside a row that already names itself. */
  label?: string;
  helper?: string;
  /**
   * Explains what the root picker measures from. No default: a section listing one row per
   * studio, tag or path rule already says it once in its own description, and a per-row copy
   * would repeat the same sentence down the whole list.
   */
  rootHelper?: string;
  templatePlaceholder?: string;
  /** Handed to the template input so the panel's at-caret token insertion can reach it. */
  templateRef?: Ref<HTMLInputElement>;
  onTemplateFocus?: () => void;
}

export function DestinationField({
  value,
  onChange,
  library,
  label = "Folder template",
  helper,
  rootHelper,
  templatePlaceholder = "$studio/$year",
  templateRef,
  onTemplateFocus,
}: Readonly<DestinationFieldProps>) {
  const { chosen, stale, showPicker, notice } = destinationPicker(value.Root, library);

  const options = [
    { value: CONTAINING_ROOT, label: CONTAINING_ROOT_LABEL },
    ...library.paths.map((path) => ({ value: path, label: path })),
    // The stale root is offered as its own option so the select shows what is actually stored rather
    // than silently reading as "the file's own library path", which is a different destination.
    ...(stale ? [{ value: value.Root, label: `${value.Root} (no longer a library path)` }] : []),
  ];

  return (
    <>
      {notice === "unreadable" ? (
        <StatusText kind="warning">
          Couldn&apos;t read Cove&apos;s library paths, so this rule can&apos;t be checked. Reload
          the page to try again.
        </StatusText>
      ) : null}
      {notice === "no-library-paths" ? (
        <StatusText kind="warning">
          Cove has no library paths configured, so there is nowhere to move files to. Add one in
          Cove&apos;s settings.
        </StatusText>
      ) : null}
      {showPicker ? (
        <Field label="Under" helper={rootHelper}>
          <Select
            // The MATCHED path, so a root stored in Cove's own platform spelling selects the library
            // path it names rather than falling off the list. The stored value is left as it is: it
            // names the right folder, and rewriting it on load would be an edit the user did not make.
            value={chosen ?? value.Root}
            onChange={(root) => {
              onChange({ ...value, Root: root });
            }}
            options={options}
          />
          {stale ? (
            <StatusText kind="error">
              This root is no longer one of Cove&apos;s library paths, so the rule is skipped. Pick
              another.
            </StatusText>
          ) : null}
        </Field>
      ) : null}
      <Field label={label} helper={helper}>
        <TextInput
          value={value.Template}
          onChange={(template) => {
            onChange({ ...value, Template: template });
          }}
          onFocus={onTemplateFocus}
          inputRef={templateRef}
          mono
          placeholder={templatePlaceholder}
        />
        {/* Reworded rather than suppressed when the picker is hidden. A typed path is still about to
            become literal folder names, and this is the only line that says so — while naming a
            control that is not on screen leaves the user nothing to act on. */}
        <PathShapeHint
          value={value.Template}
          message={
            showPicker
              ? "This is a folder template, not a path — pick the root beside it instead."
              : "This is a folder template, not a path — the whole thing becomes folder names under this destination's root."
          }
        />
      </Field>
    </>
  );
}
