/**
 * The two controlled collection editors: a list of typed-object rows, and a key-to-value map. Both
 * are render-prop generics, because the row layouts they serve have nothing in common but the
 * add/remove/reorder frame around them.
 *
 * Neither holds the collection. What they do hold is which row is which, so a remove or a reorder
 * carries a row's DOM with it rather than leaving the node in place for the next row's data.
 */
import type { ReactNode } from "react";
import { useState } from "react";
import { ChevronDown, ChevronUp, X } from "lucide-react";

import { StatusText } from "./feedback";
import { Button } from "./fields";

/** The row keys {@link ObjectArrayEditor} is using, and the list they were minted for. */
interface RowIdentity {
  source: readonly unknown[];
  keys: number[];
  nextKey: number;
}

/**
 * Keys for a list this editor has not seen before, taken from `from` onwards. A re-seed carries the
 * counter on rather than restarting at nought: a key that repeats one the previous list still holds
 * hands that row's node to whatever now sits at the repeated key's place, which is how a kept node
 * reached a row it had nothing to do with. Carrying the counter means nothing matches, so the whole
 * list is rebuilt - which is what an unrelated list deserves.
 */
function seedRowIdentity(rows: readonly unknown[], from: number): RowIdentity {
  return { source: rows, keys: rows.map((_, i) => from + i), nextKey: from + rows.length };
}

/**
 * The identity to render `rows` with: the one in hand where it still names this list, a fresh one
 * otherwise. A caller that rebuilds the array around unchanged rows each render - `rows={x ?? []}`
 * is enough - would otherwise re-seed on every render and lose the reorder, so the rows are
 * compared rather than the array holding them.
 */
function rowIdentityFor(identity: RowIdentity, rows: readonly unknown[]): RowIdentity {
  if (identity.source === rows) return identity;
  const same =
    identity.source.length === rows.length && rows.every((row, i) => identity.source[i] === row);
  return same ? { ...identity, source: rows } : seedRowIdentity(rows, identity.nextKey);
}

/**
 * Generic add/remove/reorder editor for a list of typed-object rows. Controlled (`rows`/`onChange`,
 * no internal persistence). The caller supplies the row layout via `renderRow` and a fresh blank row
 * via `makeRow`, so routing (Pattern+IsRegex+Dest), excludes (Pattern+IsRegex), and field-replacers
 * (TargetToken+Find+Replace) all reuse this one editor without forking - the render-prop shape is the
 * only thing that keeps the generic worth its weight over three near-duplicate row components.
 *
 * `renderRow` receives the row, its index, and a typed patch callback so a field edit produces a fresh
 * row object (the update never mutates `rows` in place - it rebuilds the array and calls `onChange`).
 * Reorder (`ordered`) mirrors {@link TagListInput}'s ↑↓ move; empty state renders only the add control.
 *
 * `rows` is expected back from `onChange` as the array this editor handed over, which is what tells
 * an edit here from a list arriving from elsewhere.
 */
export function ObjectArrayEditor<T>({
  rows,
  onChange,
  makeRow,
  renderRow,
  addLabel,
  ordered = false,
}: {
  rows: T[];
  onChange: (rows: T[]) => void;
  makeRow: () => T;
  renderRow: (row: T, index: number, update: (patch: Partial<T>) => void) => ReactNode;
  addLabel: string;
  ordered?: boolean;
}) {
  // A key per row, so a remove or a reorder carries that row's DOM with it instead of leaving the
  // node in place for the next row's data. The rows are arbitrary typed objects with no id, and an
  // edit rebuilds the row object, so neither the index nor the object reference is a stable key.
  //
  // The keys are only meaningful against the list they were minted for, so they are held with it.
  // Every edit below records the array it hands to `onChange`, which is the array that comes back as
  // `rows`; anything else is a list this editor did not produce - a load, a discard, a reset - whose
  // rows these keys do not name, so identity restarts. Reconciling in render rather than from an
  // effect is what keeps a key from being read while it still names the previous list, which is how
  // a kept node reached a row it had nothing to do with.
  const [identity, setIdentity] = useState(() => seedRowIdentity(rows, 0));
  const current = rowIdentityFor(identity, rows);
  if (current !== identity) setIdentity(current);

  function commit(next: T[], keys: number[], nextKey: number) {
    setIdentity({ source: next, keys, nextKey });
    onChange(next);
  }

  function update(i: number, patch: Partial<T>) {
    commit(
      rows.map((row, idx) => (idx === i ? { ...row, ...patch } : row)),
      current.keys,
      current.nextKey,
    );
  }

  function remove(i: number) {
    commit(
      rows.filter((_, idx) => idx !== i),
      current.keys.filter((_, idx) => idx !== i),
      current.nextKey,
    );
  }

  function move(i: number, dir: -1 | 1) {
    const j = i + dir;
    if (j < 0 || j >= rows.length) return;
    const next = [...rows];
    [next[i], next[j]] = [next[j], next[i]];
    const keys = [...current.keys];
    [keys[i], keys[j]] = [keys[j], keys[i]];
    commit(next, keys, current.nextKey);
  }

  function add() {
    commit([...rows, makeRow()], [...current.keys, current.nextKey], current.nextKey + 1);
  }

  return (
    <div className="space-y-2">
      {rows.map((row, i) => (
        <div
          key={current.keys[i]}
          className="flex items-start gap-2 rounded-xl border border-border bg-card p-3"
        >
          <div className="min-w-0 flex-1 space-y-2">
            {renderRow(row, i, (patch) => {
              update(i, patch);
            })}
          </div>
          {ordered ? (
            <span className="flex flex-col text-muted">
              <button
                type="button"
                aria-label={`Move row ${i + 1} up`}
                onClick={() => {
                  move(i, -1);
                }}
                className="hover:text-foreground"
              >
                <ChevronUp className="h-4 w-4" />
              </button>
              <button
                type="button"
                aria-label={`Move row ${i + 1} down`}
                onClick={() => {
                  move(i, 1);
                }}
                className="hover:text-foreground"
              >
                <ChevronDown className="h-4 w-4" />
              </button>
            </span>
          ) : null}
          <button
            type="button"
            aria-label={`Remove row ${i + 1}`}
            onClick={() => {
              remove(i);
            }}
            className="text-muted hover:text-foreground"
          >
            <X className="h-4 w-4" />
          </button>
        </div>
      ))}
      <Button variant="ghost" onClick={add}>
        {addLabel}
      </Button>
    </div>
  );
}

/**
 * Generic key→value map editor. Controlled (`map`/`onChange`, no internal persistence). Renders one
 * row per existing entry and a separate "add" row; the caller renders both the key and value controls
 * via render-props (`renderKey`/`renderValue`) so the studio/tag picker can supply a searchable key
 * control while a plain text map supplies a text input - the map editor itself stays agnostic to how a
 * key is chosen.
 *
 * De-dupe on add mirrors {@link TagListInput}: adding a key already present is refused rather than
 * silently overwriting the existing value (a silent overwrite would lose the prior mapping). The
 * pending key/value are local draft state; only a successful add commits them through `onChange`.
 * Keys render as React text nodes (auto-escaped).
 *
 * The key column is capped: a key is one short label, while a value cell can hold a whole row of
 * controls.
 */
export function KeyValueMapEditor<TValue>({
  map,
  onChange,
  emptyValue,
  renderKey,
  renderValue,
  renderKeyLabel,
  addLabel,
}: {
  map: Record<string, TValue>;
  onChange: (map: Record<string, TValue>) => void;
  // What the add row starts from, and what it returns to after a commit. Supplied rather than
  // assumed because a value is not always a string: an editor whose rows hold a composite would
  // otherwise have to invent an empty one, and two places would then disagree about what "not
  // filled in yet" is.
  emptyValue: TValue;
  renderKey: (
    draftKey: string,
    setDraftKey: (key: string) => void,
    existingKeys: readonly string[],
  ) => ReactNode;
  renderValue: (value: TValue, setValue: (value: TValue) => void) => ReactNode;
  // How a committed row's key displays. An opaque-id key (e.g. a studio id) supplies this to show a
  // human label so a saved rule reads "Studio Name → …" not "42 → …". A nullish result falls back to
  // the raw key: a renderer resolving an id it cannot find must not blank the cell, which would leave
  // the row unidentifiable and so unremovable.
  renderKeyLabel?: (key: string) => ReactNode;
  addLabel: string;
}) {
  const [draftKey, setDraftKey] = useState("");
  // The draft holds undefined until the user edits it, and `emptyValue` is read at the moment it is
  // needed rather than copied at mount. Seeding the state with `emptyValue` instead would freeze the
  // caller's first one forever: `useState` reads its argument on the first render only, so a value
  // derived from data still being fetched - a destination root derived from Cove's library paths,
  // say - would commit the placeholder the caller had before the fetch landed.
  const [draftValue, setDraftValue] = useState<TValue | undefined>(undefined);
  const pendingValue = draftValue ?? emptyValue;
  // Rows render from the entries rather than from the keys, so each row's value arrives paired with
  // its key out of one traversal instead of being looked back up by index.
  const entries = Object.entries(map);
  const keys = entries.map(([key]) => key);

  function setValue(key: string, value: TValue) {
    onChange({ ...map, [key]: value });
  }

  function remove(key: string) {
    onChange(Object.fromEntries(Object.entries(map).filter(([k]) => k !== key)));
  }

  function add() {
    const k = draftKey.trim();
    // Refuse a blank or duplicate key - a duplicate add must not clobber the existing mapping.
    if (k.length === 0 || k in map) return;
    onChange({ ...map, [k]: pendingValue });
    setDraftKey("");
    setDraftValue(undefined);
  }

  const duplicate = draftKey.trim().length > 0 && draftKey.trim() in map;

  return (
    <div className="space-y-2">
      {entries.map(([key, value]) => (
        <div
          key={key}
          className="flex items-center gap-2 rounded-xl border border-border bg-card p-3"
        >
          <span className="min-w-0 max-w-xs flex-1 truncate font-mono text-sm text-foreground">
            {renderKeyLabel?.(key) ?? key}
          </span>
          <span className="flex-1">
            {renderValue(value, (v) => {
              setValue(key, v);
            })}
          </span>
          <button
            type="button"
            aria-label={`Remove ${key}`}
            onClick={() => {
              remove(key);
            }}
            className="text-muted hover:text-foreground"
          >
            <X className="h-4 w-4" />
          </button>
        </div>
      ))}
      <div className="flex items-start gap-2 rounded-xl border border-border bg-card p-3">
        <span className="min-w-0 max-w-xs flex-1">{renderKey(draftKey, setDraftKey, keys)}</span>
        <span className="min-w-0 flex-1">{renderValue(pendingValue, setDraftValue)}</span>
        {/* Stands in for a committed row's remove button, so the add row's controls sit in the same
            columns as the rows above. */}
        <span className="w-4 shrink-0" />
      </div>
      <Button onClick={add} disabled={draftKey.trim().length === 0 || duplicate}>
        {addLabel}
      </Button>
      {duplicate ? <StatusText kind="error">That key already has a value.</StatusText> : null}
    </div>
  );
}
