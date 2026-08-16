/**
 * DestinationField — the one editor every destination in this panel uses: a ROOT chosen from Cove's
 * library paths, plus a relative folder template rendered under it.
 *
 * No path is ever typed. Cove owns the library paths, so a typed copy of one here would point at
 * nothing the moment the user changed it in Cove; a chosen root is a reference that follows.
 *
 * Presentational — the paths arrive from {@link useLibraryPaths} and every edit flows up through
 * `onChange`.
 */
import { Field, Select, TextInput, PathShapeHint, StatusText } from "@cove-extensions/ui-shared";

import { chosenLibraryPath, type Destination } from "./options";

/** The dropdown value standing for "the file's own library path" — the stored empty root. */
const CONTAINING_ROOT = "";

export interface DestinationFieldProps {
  value: Destination;
  onChange: (value: Destination) => void;
  /** Cove's configured library paths, from `/library-paths`. */
  libraryPaths: readonly string[];
  /** Shown above the template input; omit inside a row that already names itself. */
  label?: string;
  helper?: string;
  templatePlaceholder?: string;
}

export function DestinationField({
  value,
  onChange,
  libraryPaths,
  label = "Destination",
  helper,
  templatePlaceholder = "$studio / $year",
}: DestinationFieldProps) {
  // The picker is hidden when there is nothing to pick — one library path is the whole library, so
  // asking which one to use would be a question with one answer, and `newDestination` has already
  // stored that answer (see options.ts for why the path itself, not the sentinel). It comes BACK when
  // the stored root is not among the current paths, which is the state that stops the rule working:
  // hiding the control then would leave the user reading a skip reason with no way to act on it.
  const chosen = chosenLibraryPath(value.Root, libraryPaths);
  const stale = value.Root !== CONTAINING_ROOT && chosen === undefined;
  const showPicker = libraryPaths.length > 1 || stale;

  const options = [
    { value: CONTAINING_ROOT, label: "(the file's own library path)" },
    ...libraryPaths.map((path) => ({ value: path, label: path })),
    // The stale root is offered as its own option so the select shows what is actually stored rather
    // than silently reading as "the file's own library path", which is a different destination.
    ...(stale ? [{ value: value.Root, label: `${value.Root} (no longer a library path)` }] : []),
  ];

  return (
    <>
      {libraryPaths.length === 0 ? (
        <StatusText kind="warning">
          Cove has no library paths configured, so there is nowhere to move files to. Add one in
          Cove&apos;s settings.
        </StatusText>
      ) : null}
      {showPicker ? (
        <Field label="Under" helper="Which of Cove's library paths this destination measures from.">
          <Select
            // The MATCHED path, so a root stored in Cove's own platform spelling by an older build of
            // this panel selects the library path it names rather than falling off the list. The
            // stored value is left as it is: it names the right folder, and rewriting it on load
            // would be an edit the user did not make.
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
          mono
          placeholder={templatePlaceholder}
        />
        <PathShapeHint value={value.Template} />
      </Field>
    </>
  );
}
