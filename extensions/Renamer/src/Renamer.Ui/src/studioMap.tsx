/**
 * Bridges the number-keyed `StudioDestinations` field onto the string-keyed `KeyValueMapEditor`: the
 * key cell is a single-select `StudioPicker` (the picked studio's stable id), the value cell is the
 * destination-path text input. Reuses both primitives verbatim — the only new logic is the numeric-key
 * coercion (studioMapLogic.ts), kept out here so it is offline-tested in isolation.
 */
import { useEffect, useState } from "react";
import { request } from "@cove/extension-sdk";

import { KeyValueMapEditor, TextInput, PathShapeHint } from "@cove-ext/ui-shared";
import { StudioPicker } from "./entityPicker";
import { resolveStudioLabel, type EntityRef } from "./entityPickerLogic";
import { toStringKeyed, fromStringKeyed } from "./studioMapLogic";

const EXTENSION_ID = "com.alextomas955.renamer";
// The SDK's request() prepends /api, so the path must not carry it (mirrors entityPicker.tsx).
const LIST_STUDIOS_PATH = `/extensions/${EXTENSION_ID}/list-studios`;

/**
 * The studio destination-rule editor. Accepts/emits the backend `Record<number, string>`; internally
 * the map editor works string-keyed, so every edit is converted back through `fromStringKeyed` before
 * reaching the parent. The id must stay a NUMBER end to end so the persisted map is value-equal with
 * the backend field and normalizeOptions' coercion — a string key would diverge.
 *
 * A committed rule keys on the opaque studio id; the editor fetches the studio list once so a saved
 * row reads "Studio Name → …" rather than the unreadable "42 → …" (and a deleted studio's id shows as
 * a `#{id} (missing)` marker). The fetch reuses the same list-studios endpoint the picker uses and
 * degrades silently to the raw id if it fails — the label is a readability aid, not load-bearing.
 */
export function StudioDestinationsEditor({
  map,
  onChange,
}: {
  map: Record<number, string>;
  onChange: (map: Record<number, string>) => void;
}) {
  const [studios, setStudios] = useState<EntityRef[]>([]);

  // Fetch the studio list once on mount to resolve committed id keys to names. The state write lands
  // in the async .then (not the effect body), so it reads as an external-data load, not a synchronous
  // render-driven setState. The `live` guard drops a late response after unmount.
  useEffect(() => {
    let live = true;
    request<EntityRef[]>(LIST_STUDIOS_PATH)
      .then((rows) => {
        if (live) setStudios(rows);
      })
      .catch(() => {
        // A failed list leaves the raw id showing — the same graceful degradation the picker uses.
      });
    return () => {
      live = false;
    };
  }, []);

  return (
    <KeyValueMapEditor
      map={toStringKeyed(map)}
      onChange={(next) => {
        onChange(fromStringKeyed(next));
      }}
      renderKey={(draftKey, setDraftKey, existingKeys) => (
        <StudioKeyCell draftKey={draftKey} setDraftKey={setDraftKey} existingKeys={existingKeys} />
      )}
      renderValue={(value, setValue) => (
        <>
          <TextInput value={value} onChange={setValue} placeholder="Destination root" />
          <PathShapeHint value={value} />
        </>
      )}
      renderKeyLabel={(key) => resolveStudioLabel(Number(key), studios)}
      addLabel="Add studio rule"
    />
  );
}

/**
 * The add-row key cell: `StudioPicker` driven single-select. The picker is multi-value, so it is fed
 * the current draft id (none or one) and on pick takes the LATEST id — the last element of the array —
 * and writes it back as the stringified key the map editor expects. Last-id-wins keeps a second pick
 * from accumulating a multi-selection the single-key map cannot hold.
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
  // The map keys arrive stringified (KeyValueMapEditor is string-keyed); the picker stores ids as
  // numbers, so coerce the already-used keys back to numbers to exclude a studio that already has a rule.
  const usedIds = existingKeys.map(Number);
  return (
    <StudioPicker
      label=""
      values={current}
      onChange={(values) => {
        const latest = values.at(-1);
        setDraftKey(latest === undefined ? "" : String(latest));
      }}
      placeholder="Search studios…"
      excludeValues={usedIds}
    />
  );
}
