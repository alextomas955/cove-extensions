/**
 * Bridges the number-keyed `StudioDestinations` field onto the string-keyed `KeyValueMapEditor`: the
 * key cell is a single-select entity field over the picked studio's stable id, the value cell is the
 * shared destination editor. Reuses both primitives verbatim — the only new logic is the numeric-key
 * coercion, which lives in options.ts beside the load-path coercion it has to agree with.
 */
import { EntityReferenceValue } from "@cove/runtime/components";

import { KeyValueMapEditor } from "@cove-extensions/ui-shared";
import { EntitySelectField } from "./EntitySelectField";
import { DestinationField } from "./DestinationField";
import {
  toStringKeyed,
  fromStringKeyed,
  newDestination,
  type Destination,
  type LibraryPathsState,
} from "./options";

/**
 * The studio destination-rule editor. Accepts/emits the backend `Record<number, string>`; internally
 * the map editor works string-keyed, so every edit is converted back through `fromStringKeyed` before
 * reaching the parent. The id must stay a NUMBER end to end so the persisted map is value-equal with
 * the backend field and normalizeOptions' coercion — a string key would diverge.
 *
 * A committed rule keys on the opaque studio id and the host resolves that id to a name, so this
 * editor holds no entity list of its own. That is one cached lookup per configured rule — bounded by
 * the rules the user authored, never by the size of the library.
 */
export function StudioDestinationsEditor({
  map,
  onChange,
  library,
}: {
  map: Record<number, Destination>;
  onChange: (map: Record<number, Destination>) => void;
  library: LibraryPathsState;
}) {
  return (
    <KeyValueMapEditor
      map={toStringKeyed(map)}
      emptyValue={newDestination(library.paths)}
      onChange={(next) => {
        onChange(fromStringKeyed(next));
      }}
      renderKey={(draftKey, setDraftKey, existingKeys) => (
        <StudioKeyCell draftKey={draftKey} setDraftKey={setDraftKey} existingKeys={existingKeys} />
      )}
      renderValue={(value, setValue) => (
        <DestinationField value={value} onChange={setValue} library={library} label="Folder" />
      )}
      renderKeyLabel={(key) => <EntityReferenceValue entityType="studio" value={Number(key)} />}
      addLabel="Add studio rule"
    />
  );
}

/**
 * The add-row key cell: a single-select driven from the multi-value selector. It is fed the current
 * draft id (none or one) and on pick takes the LATEST id — the last element of the array — and writes
 * it back as the stringified key the map editor expects. Last-id-wins keeps a second pick from
 * accumulating a multi-selection the single-key map cannot hold.
 */
function StudioKeyCell({
  draftKey,
  setDraftKey,
  existingKeys,
}: {
  draftKey: string;
  setDraftKey: (key: string) => void;
  existingKeys: readonly string[];
}) {
  const current = draftKey === "" ? [] : [Number(draftKey)];
  // The map keys arrive stringified (KeyValueMapEditor is string-keyed); the selector works in ids as
  // numbers, so coerce the already-used keys back to numbers to exclude a studio that already has a rule.
  const usedIds = existingKeys.map(Number);
  return (
    <EntitySelectField
      entityType="studio"
      label=""
      values={current}
      onChange={(values) => {
        const latest = values.at(-1);
        setDraftKey(latest === undefined ? "" : String(latest));
      }}
      placeholder="Search studios…"
      excludeIds={usedIds}
    />
  );
}
