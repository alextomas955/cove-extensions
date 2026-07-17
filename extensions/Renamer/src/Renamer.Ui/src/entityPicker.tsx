/**
 * The searchable, async-backed studio/tag picker: a text input over a filtered results list (NOT a
 * native `<select>`, which can't do typeahead), built on the backend's list-studios/list-tags
 * endpoints. Sibling to primitives.tsx; reuses its class vocabulary verbatim so the picker is
 * host-native.
 *
 * Studios store the stable id (never the display name, so a later rename can't mis-target a renamed
 * studio); tags store the library's canonical name (the backend keys tags case-insensitively). The
 * filter and the stale-id resolution come from the tested entityPickerLogic.ts helpers — the
 * component never re-implements them.
 */
import { useCallback, useEffect, useId, useRef, useState } from "react";
import { X } from "lucide-react";
import { request } from "@cove/extension-sdk";

import { Field, StatusText, Spinner } from "@cove-ext/ui-shared";
import {
  filterEntities,
  excludeEntities,
  resolveStudioLabel,
  isResolvedStudioId,
  canonicalTagName,
  type EntityRef,
} from "./entityPickerLogic";

const EXTENSION_ID = "com.alextomas955.renamer";
// The SDK's request() PREPENDS /api, so the path must NOT carry it (mirrors renameSelected.ts).
const LIST_STUDIOS_PATH = `/extensions/${EXTENSION_ID}/list-studios`;
const LIST_TAGS_PATH = `/extensions/${EXTENSION_ID}/list-tags`;
const LIST_PERFORMERS_PATH = `/extensions/${EXTENSION_ID}/list-performers`;

const INPUT_CLASS =
  "w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none";
const RESULT_CLASS =
  "cursor-pointer rounded-lg px-2 py-1 text-left text-sm text-foreground hover:bg-card-hover";
const CHIP_BASE =
  "inline-flex items-center gap-1 rounded-lg border border-border bg-card px-2 py-0.5 text-xs text-foreground";
const CHIP_STALE = "border-red-400 text-red-400";

/**
 * How a picked {@link EntityRef} becomes a stored value, and how a stored value resolves back to a
 * display label — the only two things that differ between the studio picker (id) and the tag picker
 * (canonical name). Keeping them in one adapter lets the generic body stay shape-agnostic.
 */
interface EntityAdapter<V> {
  /** The value stored when the user picks this row (studio → id; tag → canonical name). */
  toValue: (entity: EntityRef, fetched: readonly EntityRef[]) => V;
  /**
   * The stored value a result row maps to, WITHOUT the fetched list — used to drop rows already used
   * elsewhere (the no-duplicate-key filter). Distinct from {@link toValue} because exclusion runs over
   * the live result rows, where the row IS the entity, so the list-dependent canonicalization
   * {@link toValue} performs is unnecessary.
   */
  valueOf: (entity: EntityRef) => V;
  /** The label a stored value shows (studio → resolveStudioLabel; tag → the name itself). */
  toLabel: (value: V, fetched: readonly EntityRef[]) => string;
  /** Whether a stored value resolves to a live entry (drives stale-chip styling). */
  isResolved: (value: V, fetched: readonly EntityRef[]) => boolean;
}

/**
 * Generic controlled picker: a text input + a lazily-fetched, filtered results panel, with the
 * already-selected values rendered above as removable chips. Controlled (`values`/`onChange`); it
 * does not persist the selection itself.
 *
 * The list is small reference data, so it is fetched once on first open and cached for the mount
 * (one fetch per picker instance). A fetch failure does NOT crash the panel — it surfaces a quiet
 * error and still lets the user keep/remove existing values and type-ahead over whatever is cached.
 */
function EntityPicker<V>({
  label,
  helper,
  values,
  onChange,
  endpointPath,
  adapter,
  placeholder,
  excludeValues,
}: {
  label: string;
  helper?: string;
  values: V[];
  onChange: (values: V[]) => void;
  endpointPath: string;
  adapter: EntityAdapter<V>;
  placeholder?: string;
  // Stored values to omit from the results (e.g. studios/tags already used as a map key elsewhere),
  // so the picker can supply a NEW key without offering a duplicate. Optional; absent = no exclusion.
  excludeValues?: readonly V[];
}) {
  const id = useId();
  const [query, setQuery] = useState("");
  const [open, setOpen] = useState(false);
  const [fetched, setFetched] = useState<EntityRef[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [loading, setLoading] = useState(false);
  const [failed, setFailed] = useState(false);
  // Guards the in-flight fetch so re-focusing never fires a second concurrent request. It is NOT the
  // "already loaded" flag: a failed load must be retryable, so this resets in catch and the
  // load-succeeded state lives in `loaded` instead.
  const loadingRef = useRef(false);
  const containerRef = useRef<HTMLDivElement>(null);

  // Close the results panel on a click anywhere outside this control — Cove's own searchable selects
  // (GlobalSearch, SavedFilterMenu) close this way via a document mousedown that checks containment,
  // rather than onBlur (which would fire before a result's onClick registers and swallow the pick).
  useEffect(() => {
    if (!open) return;
    const onMouseDown = (e: MouseEvent) => {
      if (!containerRef.current?.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener("mousedown", onMouseDown);
    return () => {
      document.removeEventListener("mousedown", onMouseDown);
    };
  }, [open]);

  const load = useCallback(async () => {
    if (loadingRef.current || loaded) return;
    loadingRef.current = true;
    setLoading(true);
    try {
      const rows = await request<EntityRef[]>(endpointPath);
      setFetched(rows);
      setLoaded(true);
      setFailed(false);
    } catch {
      // A backend ApiError (403/500) and a generic network failure degrade the same way — the panel
      // must stay usable (existing values keep/remove) on either — so every failure collapses to one
      // quiet error flag rather than branching on the error kind. `loaded` stays false so a stored
      // value is NOT mislabeled "missing" (we have no list to disprove it) and reopening retries.
      setFailed(true);
    } finally {
      loadingRef.current = false;
      setLoading(false);
    }
  }, [endpointPath, loaded]);

  function openAndLoad() {
    setOpen(true);
    void load();
  }

  function pick(entity: EntityRef) {
    const value = adapter.toValue(entity, fetched);
    if (!values.includes(value)) onChange([...values, value]);
    setQuery("");
    setOpen(false);
  }

  function remove(value: V) {
    onChange(values.filter((v) => v !== value));
  }

  // Drop already-used values BEFORE the query filter so a used row never shows even on an exact-name
  // search, and so Enter-picks-top-match can't land on one. Two sources of "used": this picker's OWN
  // selected `values` (don't offer a chip the user already added) and the caller's `excludeValues`
  // (e.g. studios/tags already used as a destination key elsewhere).
  const used = excludeValues ? [...values, ...excludeValues] : values;
  const selectable = excludeEntities(fetched, used, adapter.valueOf);
  const matches = filterEntities(query, selectable);

  return (
    <Field label={label} helper={helper}>
      {values.length > 0 ? (
        <div className="mb-1 flex flex-wrap gap-1">
          {values.map((value) => {
            // Only mark a value stale once a load has actually succeeded — without a list to check
            // against, every value would falsely render "missing" and could scare a user into
            // deleting a still-valid rule.
            const stale = loaded && !adapter.isResolved(value, fetched);
            return (
              <span
                key={String(value)}
                className={stale ? `${CHIP_BASE} ${CHIP_STALE}` : CHIP_BASE}
              >
                <span>{adapter.toLabel(value, fetched)}</span>
                <button
                  type="button"
                  aria-label={`Remove ${adapter.toLabel(value, fetched)}`}
                  onClick={() => {
                    remove(value);
                  }}
                  className="text-muted hover:text-foreground"
                >
                  <X className="h-3 w-3" />
                </button>
              </span>
            );
          })}
        </div>
      ) : null}
      <div className="relative" ref={containerRef}>
        <input
          id={id}
          type="text"
          value={query}
          placeholder={placeholder}
          className={INPUT_CLASS}
          onFocus={openAndLoad}
          onChange={(e) => {
            setQuery(e.target.value);
            setOpen(true);
          }}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              // Enter picks the top match only with an active query; on an empty query it would
              // silently add whatever happens to sort first, which the user never aimed at.
              if (query.trim() !== "" && matches.length > 0) pick(matches[0]);
            } else if (e.key === "Escape") {
              setOpen(false);
            }
          }}
        />
        {open && !failed ? (
          <div className="mt-1 flex max-h-48 flex-col gap-0.5 overflow-auto rounded-xl border border-border bg-card p-1">
            {loading ? (
              <span className="flex items-center gap-2 px-2 py-1 text-sm text-muted">
                <Spinner />
                Loading…
              </span>
            ) : matches.length === 0 ? (
              <span className="px-2 py-1 text-sm text-muted">No matches</span>
            ) : (
              matches.map((entity) => (
                <button
                  key={entity.id}
                  type="button"
                  className={RESULT_CLASS}
                  onClick={() => {
                    pick(entity);
                  }}
                >
                  {entity.name}
                </button>
              ))
            )}
          </div>
        ) : null}
      </div>
      {failed ? (
        <span className="mt-1 block">
          <StatusText kind="error">
            Could not load the list — existing values stay editable.
          </StatusText>
        </span>
      ) : null}
    </Field>
  );
}

const STUDIO_ADAPTER: EntityAdapter<number> = {
  toValue: (entity) => entity.id,
  valueOf: (entity) => entity.id,
  toLabel: (value, fetched) => resolveStudioLabel(value, fetched),
  isResolved: (value, fetched) => isResolvedStudioId(value, fetched),
};

const TAG_ADAPTER: EntityAdapter<string> = {
  // A picked row already carries the canonical spelling; canonicalTagName also folds a typed casing.
  toValue: (entity, fetched) => canonicalTagName(entity.name, fetched),
  valueOf: (entity) => entity.name,
  toLabel: (value) => value,
  // A tag value is the name itself, so it is always displayable; "resolved" tracks list membership.
  isResolved: (value, fetched) => fetched.some((e) => e.name.toLowerCase() === value.toLowerCase()),
};

// Performers key on NAME (the whitelist/blacklist match by name), so this mirrors the tag adapter:
// a picked row carries its canonical name, and a stored value resolves by case-insensitive name.
const PERFORMER_ADAPTER: EntityAdapter<string> = {
  toValue: (entity, fetched) => canonicalTagName(entity.name, fetched),
  valueOf: (entity) => entity.name,
  toLabel: (value) => value,
  isResolved: (value, fetched) => fetched.some((e) => e.name.toLowerCase() === value.toLowerCase()),
};

/** Studio picker: stores the stable studio id; a stale id renders as a removable `#{id} (missing)` chip. */
export function StudioPicker({
  label,
  helper,
  values,
  onChange,
  placeholder,
  excludeValues,
}: {
  label: string;
  helper?: string;
  values: number[];
  onChange: (values: number[]) => void;
  placeholder?: string;
  excludeValues?: readonly number[];
}) {
  return (
    <EntityPicker
      label={label}
      helper={helper}
      values={values}
      onChange={onChange}
      endpointPath={LIST_STUDIOS_PATH}
      adapter={STUDIO_ADAPTER}
      placeholder={placeholder}
      excludeValues={excludeValues}
    />
  );
}

/** Tag picker: stores the library's canonical tag name (matching the backend's case-insensitive keying). */
export function TagPicker({
  label,
  helper,
  values,
  onChange,
  placeholder,
  excludeValues,
}: {
  label: string;
  helper?: string;
  values: string[];
  onChange: (values: string[]) => void;
  placeholder?: string;
  excludeValues?: readonly string[];
}) {
  return (
    <EntityPicker
      label={label}
      helper={helper}
      values={values}
      onChange={onChange}
      endpointPath={LIST_TAGS_PATH}
      adapter={TAG_ADAPTER}
      placeholder={placeholder}
      excludeValues={excludeValues}
    />
  );
}

/** Performer picker: stores the performer's canonical name (whitelist/blacklist match by name). */
export function PerformerPicker({
  label,
  helper,
  values,
  onChange,
  placeholder,
  excludeValues,
}: {
  label: string;
  helper?: string;
  values: string[];
  onChange: (values: string[]) => void;
  placeholder?: string;
  excludeValues?: readonly string[];
}) {
  return (
    <EntityPicker
      label={label}
      helper={helper}
      values={values}
      onChange={onChange}
      endpointPath={LIST_PERFORMERS_PATH}
      adapter={PERFORMER_ADAPTER}
      placeholder={placeholder}
      excludeValues={excludeValues}
    />
  );
}
