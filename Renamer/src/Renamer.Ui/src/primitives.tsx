/**
 * Field primitives re-implemented locally (the host's `SettingsPrimitives.tsx` is not
 * importable from an extension bundle). Every class string is matched byte-for-byte to
 * Cove's own primitives so the panel is typographically/spacing-wise indistinguishable
 * from native Cove settings — and so every utility resolves against the host's already-
 * emitted Tailwind stylesheet (no CSS bundle ships).
 *
 * Focus treatment uses Cove's convention `focus:border-accent focus:outline-none` — NOT the
 * `focus-visible:ring-*` utilities, which the host stylesheet does not emit (so they would do nothing).
 *
 * Import audit (checked directly against `@cove/runtime/components`, not assumed): none of its
 * exports are a drop-in for these primitives. `SettingsPrimitives.tsx` — the host's own field/
 * control set these mirror — is never re-exported to extensions at all; only three host-internal
 * pages import it directly. The barrel offers entity-browsing (`ListPage`, `VideoCard`,
 * `DetailListToolbar`, `Pager`), dialog (`ConfirmDialog`, `EditModal`), and formatting
 * (`TagBadge`, `formatDuration`, `formatFileSize`, `formatDate`, `getResolutionLabel`,
 * `CustomFieldsDisplay`/`Editor`) utilities, none of which overlap a settings-field primitive's
 * shape. `TagBadge` is a tag/label pill with color and provenance — this codebase's status pills
 * (`WarningBadge.tsx`) key off a rename-status enum instead, a different concept, not a swap.
 * `formatDuration`/`formatFileSize`/`formatDate`/`getResolutionLabel` have no local counterpart
 * anywhere in this directory: nothing here renders a raw duration, file size, or date value, and
 * `CustomFieldsDisplay`/`Editor` render Cove's custom-fields feature, which this extension has no
 * UI for. The bare `react`/`react-dom`/`lucide-react`/`@tanstack/react-query` import specifiers
 * used throughout this codebase are not a migration gap either: the host's `legacySpecifiers`
 * alias table (`extension-runtime-contract.ts`) resolves each of them to the identical runtime-
 * injected module `@cove/runtime/react` etc. resolve to, so there is no behavior difference and no
 * reason to change the specifier string. See `dialog.tsx`'s header for why `ConfirmDialog` isn't a
 * swap for `Dialog` either.
 */
import type { ReactNode } from "react";
import { useId, useRef, useState, useEffect } from "react";
import { Loader2, X, ChevronUp, ChevronDown } from "lucide-react";
import { isRegexValid, isAbsolutePathShape } from "./primitivesLogic";
import { availableOptions, type ValueOption } from "./entityPickerLogic";

const INPUT_CLASS =
  "w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none";

/**
 * Selectable chip/button styling, matching the TokenLegend / PresetRow chip. Host-compiled classes
 * only — no arbitrary `[…]` values, because the host's Tailwind JIT never scans this bundle.
 *
 * The selected and unselected states must NOT share a conflicting color utility. Tailwind resolves
 * two utilities targeting the same property by their order in the generated stylesheet, NOT by the
 * order in the class attribute — and the host emits `.bg-card` / `.text-foreground` / `.border-border`
 * AFTER `.bg-accent` / `.text-accent` / `.border-accent`. So a selected state built by APPENDING accent
 * utilities to the base chip (which carries the card/foreground/border ones) loses every color
 * conflict and renders identically to unselected — the bug that made the selection invisible.
 *
 * The fix: a color-free shape base, plus two MUTUALLY EXCLUSIVE color sets. {@link chipClass} picks one
 * — never both — so no same-property conflict exists and the host's source order is irrelevant.
 */
const CHIP_BASE = "cursor-pointer rounded-lg border px-2 py-1 text-xs";
const CHIP_UNSELECTED =
  "border-border bg-card text-foreground hover:border-accent/50 hover:text-accent";
// Cove's own chip/pill selected state is an accent TINT, not a solid fill — `border-accent bg-accent/15`
// (see CustomFields enum chips / BookmarkButton). The solid `bg-accent text-white` is Cove's idiom for
// segmented TOGGLE buttons, not chips, and reads too heavy here. The tint still carries selection on the
// BACKGROUND (a property the unselected set's text/border utilities don't contest), so it stays visible
// regardless of the host stylesheet's source order — the conflict that hid an earlier border/text tint.
const CHIP_SELECTED = "border-accent bg-accent/15 text-foreground";

/** The full chip class for a selected/unselected chip — one color set, never both. */
function chipClass(selected: boolean): string {
  return `${CHIP_BASE} ${selected ? CHIP_SELECTED : CHIP_UNSELECTED}`;
}

/** Sentinel `value` for the "Custom…" option in {@link ExampleSelect}. */
const CUSTOM_SENTINEL = "__custom__";

/** Label + control + optional helper. Matches Cove `SettingsField`. */
export function Field({
  label,
  helper,
  children,
}: {
  label: string;
  helper?: string;
  children: ReactNode;
}) {
  return (
    <label className="block text-sm" title={helper}>
      <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-muted">
        {label}
      </span>
      {children}
      {helper ? <span className="mt-1 block text-xs text-secondary">{helper}</span> : null}
    </label>
  );
}

export function TextInput({
  value,
  onChange,
  onFocus,
  placeholder,
  mono = false,
  inputRef,
}: {
  value: string;
  onChange: (value: string) => void;
  onFocus?: () => void;
  placeholder?: string;
  mono?: boolean;
  inputRef?: React.Ref<HTMLInputElement>;
}) {
  return (
    <input
      ref={inputRef}
      type="text"
      value={value}
      placeholder={placeholder}
      onChange={(e) => {
        onChange(e.target.value);
      }}
      onFocus={onFocus}
      className={mono ? `${INPUT_CLASS} font-mono` : INPUT_CLASS}
    />
  );
}

export function NumberInput({
  value,
  onChange,
  min,
}: {
  value: number;
  onChange: (value: number) => void;
  min?: number;
}) {
  return (
    <input
      type="number"
      value={Number.isNaN(value) ? "" : value}
      min={min}
      onChange={(e) => {
        onChange(e.target.value === "" ? 0 : Number(e.target.value));
      }}
      className={`themed-number-input ${INPUT_CLASS}`}
    />
  );
}

export function Select<T extends string>({
  value,
  onChange,
  options,
}: {
  value: T;
  onChange: (value: T) => void;
  options: readonly { value: T; label: string }[];
}) {
  return (
    <select
      value={value}
      onChange={(e) => {
        onChange(e.target.value as T);
      }}
      className={INPUT_CLASS}
    >
      {options.map((o) => (
        <option key={o.value} value={o.value}>
          {o.label}
        </option>
      ))}
    </select>
  );
}

/** One known format/pattern option for {@link ExampleSelect}. */
export interface ExampleOption {
  /** The canonical value persisted into RenameOptions (e.g. `"yyyy-MM-dd"`). */
  value: string;
  /** Static, hand-authored reference example shown after the arrow (e.g. `"2026-03-12"`). */
  example: string;
}

/**
 * A labelled native `<select>` of common format/pattern strings, each option
 * folding a static reference example into its text (`{value} → {example}`), plus a `Custom…`
 * sentinel that reveals the existing mono {@link TextInput} pre-filled with the current value.
 *
 * The canonical persisted value never changes shape — this is purely how the user produces the
 * string. On a value that matches no known option, `Custom…` is auto-selected and the input shown
 * (so a previously-customised value is never lost). Below the select, a `font-mono` helper line
 * restates the currently-selected option's example. All text renders as React text nodes
 * (auto-escaped; T-10-03) — no raw-HTML rendering.
 */
export function ExampleSelect({
  value,
  onChange,
  options,
  customPlaceholder,
}: {
  value: string;
  onChange: (value: string) => void;
  options: readonly ExampleOption[];
  customPlaceholder?: string;
}) {
  const matched = options.find((o) => o.value === value);
  const isCustom = matched === undefined;
  // Which <select> option is shown: the matching value, or the Custom… sentinel.
  const selectValue = isCustom ? CUSTOM_SENTINEL : value;
  // Helper-line example: the matched option's reference example, or the live custom string.
  const helperExample = matched ? `${matched.value} → ${matched.example}` : value;

  return (
    <div>
      <select
        value={selectValue}
        onChange={(e) => {
          const v = e.target.value;
          // Choosing Custom…: keep the current value if it's already custom, else seed empty so
          // the revealed input starts blank for a fresh custom entry.
          if (v === CUSTOM_SENTINEL) {
            if (!isCustom) onChange("");
          } else {
            onChange(v);
          }
        }}
        className={INPUT_CLASS}
      >
        {options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.value} → {o.example}
          </option>
        ))}
        <option value={CUSTOM_SENTINEL}>Custom…</option>
      </select>
      {isCustom ? (
        <div className="mt-2">
          <TextInput value={value} onChange={onChange} placeholder={customPlaceholder} mono />
        </div>
      ) : (
        <span className="mt-1 block font-mono text-xs text-secondary">{helperExample}</span>
      )}
    </div>
  );
}

/** One known separator option for {@link SeparatorChips}. */
export interface SeparatorOption {
  /** The canonical separator value persisted (e.g. `", "`). */
  value: string;
  /** Human label with the literal whitespace made visible (e.g. `"Comma + space ( , )"`). */
  label: string;
}

/**
 * A `flex flex-wrap gap-1` quick-pick chip row of common separators plus a `Custom`
 * chip. The active separator's chip is persistently selected (the filled-accent {@link chipClass}).
 * Each chip label makes leading/trailing whitespace visible (never an apparently-empty chip).
 * The `Custom` chip reveals the existing mono {@link TextInput}, pre-filled, when the saved value
 * matches no preset. Binds the SAME separator string via `onChange` — no shape change.
 */
export function SeparatorChips({
  value,
  onChange,
  options,
  customPlaceholder,
}: {
  value: string;
  onChange: (value: string) => void;
  options: readonly SeparatorOption[];
  customPlaceholder?: string;
}) {
  const isCustom = !options.some((o) => o.value === value);

  return (
    <div>
      <div className="flex flex-wrap gap-1">
        {options.map((o) => {
          const selected = o.value === value;
          return (
            <button
              key={o.value || "__empty__"}
              type="button"
              onClick={() => {
                onChange(o.value);
              }}
              className={chipClass(selected)}
            >
              {o.label}
            </button>
          );
        })}
        <button
          type="button"
          onClick={() => {
            // Selecting Custom from a preset clears to a fresh custom entry; if already custom, keep.
            if (!isCustom) onChange("");
          }}
          className={chipClass(isCustom)}
        >
          Custom
        </button>
      </div>
      {isCustom ? (
        <div className="mt-2">
          <TextInput value={value} onChange={onChange} placeholder={customPlaceholder} mono />
        </div>
      ) : null}
    </div>
  );
}

/**
 * A two-option segmented control: option A (strip/keep) sets the bound value to `""`;
 * option B ("Replace with") conditionally renders a short inline mono {@link TextInput} bound to
 * the value. Selecting B on an empty value focuses the input. Switching back to A resets the value
 * to `""` and removes the input. Reuses the chip visual vocabulary (selected = the filled-accent
 * {@link chipClass}). Labels and helper copy are caller-supplied. The reveal is
 * conditionally rendered (`mt-2`), not `hidden`.
 */
export function SegmentedReplace({
  value,
  onChange,
  stripLabel,
  replaceLabel,
  stripHelper,
  replaceHelper,
  inputPlaceholder,
}: {
  value: string;
  onChange: (value: string) => void;
  stripLabel: string;
  replaceLabel: string;
  stripHelper?: string;
  replaceHelper?: string;
  inputPlaceholder?: string;
}) {
  // "Replace with" is active when a non-empty value is set OR the user explicitly entered replace mode.
  // Tracking an explicit mode (not deriving it purely from value !== "") is required: clicking "Replace
  // with" on an empty value must REVEAL the input so the user can type — deriving from value alone would
  // deadlock (the input only renders when non-empty, but you can't set a value without the input).
  const inputRef = useRef<HTMLInputElement>(null);
  // replaceMode is the explicit UI mode. It is needed (not derived purely from value !== "") because
  // clicking "Replace with" on an empty value must REVEAL the input so the user can type — deriving from
  // value alone deadlocks. When the user is in replace mode and the input is momentarily empty, we stay in
  // replace mode (don't collapse mid-type). The key WR-01 case is an EXTERNAL change to "" (Reset-to-
  // defaults / load): we detect "value changed to empty since last render" and snap back to strip mode.
  const [replaceMode, setReplaceMode] = useState(value !== "");
  const prevValue = useRef(value);
  // Sync the explicit UI mode to an EXTERNAL value change (Reset/load), detected via prevValue.
  // This is a prop-change synchronization across renders, not a render-derived setState — the
  // react-compiler set-state-in-effect heuristic flags it but the ref-guarded transition is correct.
  /* eslint-disable react-hooks/set-state-in-effect */
  useEffect(() => {
    if (value !== "") {
      setReplaceMode(true); // a non-empty value always means replace mode
    } else if (prevValue.current !== "") {
      // value transitioned non-empty → "" from OUTSIDE this control (Reset/load) → return to strip mode.
      setReplaceMode(false);
    }
    prevValue.current = value;
  }, [value]);
  /* eslint-enable react-hooks/set-state-in-effect */
  const replaceActive = replaceMode || value !== "";

  function chooseStrip() {
    setReplaceMode(false);
    if (value !== "") onChange("");
  }
  function chooseReplace() {
    setReplaceMode(true);
    // Reveal the (possibly empty) input and focus it so the user can type the replacement.
    requestAnimationFrame(() => inputRef.current?.focus());
  }

  return (
    <div>
      <div className="flex gap-1">
        <button type="button" onClick={chooseStrip} className={chipClass(!replaceActive)}>
          {stripLabel}
        </button>
        <button type="button" onClick={chooseReplace} className={chipClass(replaceActive)}>
          {replaceLabel}
        </button>
      </div>
      {replaceActive ? (
        <div className="mt-2">
          <TextInput
            value={value}
            onChange={onChange}
            placeholder={inputPlaceholder}
            inputRef={inputRef}
            mono
          />
          {replaceHelper ? (
            <span className="mt-1 block text-xs text-secondary">{replaceHelper}</span>
          ) : null}
        </div>
      ) : stripHelper ? (
        <span className="mt-1 block text-xs text-secondary">{stripHelper}</span>
      ) : null}
    </div>
  );
}

export function Checkbox({
  label,
  checked,
  onChange,
  helper,
}: {
  label: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
  helper?: string;
}) {
  const id = useId();
  return (
    <div>
      <label htmlFor={id} className="flex items-center gap-2 text-sm text-secondary" title={helper}>
        <input
          id={id}
          type="checkbox"
          checked={checked}
          onChange={(e) => {
            onChange(e.target.checked);
          }}
          className="h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
        />
        <span>{label}</span>
      </label>
      {helper ? <p className="mt-1 text-xs text-secondary">{helper}</p> : null}
    </div>
  );
}

export function Toggle({
  label,
  checked,
  onChange,
  helper,
}: {
  label: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
  helper?: string;
}) {
  return (
    <div>
      <label className="flex items-center gap-2 text-sm text-secondary" title={helper}>
        <button
          type="button"
          role="switch"
          aria-checked={checked}
          onClick={() => {
            onChange(!checked);
          }}
          className={`inline-flex h-5 w-9 items-center rounded-full transition-colors ${
            checked ? "bg-accent" : "bg-card border border-border"
          }`}
        >
          <span
            className={`inline-block h-4 w-4 rounded-full bg-white transition-transform ${
              checked ? "translate-x-4" : "translate-x-0.5"
            }`}
          />
        </button>
        <span>{label}</span>
      </label>
      {helper ? <p className="mt-1 text-xs text-secondary">{helper}</p> : null}
    </div>
  );
}

/**
 * String-list editor: chips above an add-on-Enter input. Used for Whitelist / Blacklist /
 * RequiredFields / DropOrder. DropOrder additionally gets up/down reordering (`ordered`).
 */
export function TagListInput({
  values,
  onChange,
  placeholder,
  ordered = false,
  normalize,
  onReject,
  onLiveChange,
}: {
  values: string[];
  onChange: (values: string[]) => void;
  placeholder?: string;
  ordered?: boolean;
  normalize?: (raw: string) => string;
  onReject?: (candidate: string) => boolean;
  onLiveChange?: (raw: string) => void;
}) {
  const id = useId();

  function addFrom(input: HTMLInputElement) {
    const v = (normalize ? normalize(input.value) : input.value).trim();
    if (v.length === 0) return;
    if (onReject?.(v)) return;
    if (!values.includes(v)) onChange([...values, v]);
    input.value = "";
  }

  function remove(i: number) {
    onChange(values.filter((_, idx) => idx !== i));
  }

  function move(i: number, dir: -1 | 1) {
    const j = i + dir;
    if (j < 0 || j >= values.length) return;
    const next = [...values];
    [next[i], next[j]] = [next[j], next[i]];
    onChange(next);
  }

  return (
    <div>
      {values.length > 0 ? (
        <div className="mb-1 flex flex-wrap gap-1">
          {values.map((v, i) => (
            <span
              key={`${v}-${i}`}
              className="inline-flex items-center gap-1 rounded-lg border border-border bg-card px-2 py-0.5 text-xs text-foreground"
            >
              {ordered ? (
                <>
                  <button
                    type="button"
                    aria-label={`Move ${v} up`}
                    onClick={() => {
                      move(i, -1);
                    }}
                    className="text-muted hover:text-foreground"
                  >
                    ↑
                  </button>
                  <button
                    type="button"
                    aria-label={`Move ${v} down`}
                    onClick={() => {
                      move(i, 1);
                    }}
                    className="text-muted hover:text-foreground"
                  >
                    ↓
                  </button>
                </>
              ) : null}
              <span className="font-mono">{v}</span>
              <button
                type="button"
                aria-label={`Remove ${v}`}
                onClick={() => {
                  remove(i);
                }}
                className="text-muted hover:text-foreground"
              >
                <X className="h-3 w-3" />
              </button>
            </span>
          ))}
        </div>
      ) : null}
      <input
        id={id}
        type="text"
        placeholder={placeholder}
        className={INPUT_CLASS}
        onChange={(e) => {
          onLiveChange?.(e.target.value);
        }}
        onKeyDown={(e) => {
          if (e.key === "Enter") {
            e.preventDefault();
            addFrom(e.currentTarget);
          }
        }}
        onBlur={(e) => {
          addFrom(e.currentTarget);
        }}
      />
    </div>
  );
}

/**
 * A toggle-chip multiselect over a FIXED option set where order does not matter (the ignore-genders
 * list): clicking a chip toggles its membership, selected chips carry the accent tint via
 * {@link chipClass}. Stores the option VALUES (e.g. the gender enum names), in the option set's order
 * rather than click order — so two configs with the same members serialize identically. Every label
 * is a React text node (auto-escaped).
 *
 * A stored value NOT in `options` (e.g. one saved via the old free-text control, or a gender this
 * build doesn't list) is PRESERVED, not silently dropped: it renders as an extra removable chip and
 * toggling a known option keeps it. Otherwise the first toggle would erase a value the user can't see.
 */
export function ChipMultiSelect({
  options,
  values,
  onChange,
}: {
  options: readonly ValueOption[];
  values: string[];
  onChange: (values: string[]) => void;
}) {
  const optionValues = new Set(options.map((o) => o.value));
  // Stored values outside the offered set — kept verbatim so a toggle never erases them.
  const extras = values.filter((v) => !optionValues.has(v));

  function toggle(value: string) {
    const has = values.includes(value);
    // Rebuild known members in the option set's order (order-stable), then re-append any extras so
    // an out-of-set stored value survives the edit.
    const known = options
      .map((o) => o.value)
      .filter((v) => (v === value ? !has : values.includes(v)));
    onChange([...known, ...extras]);
  }

  function removeExtra(value: string) {
    onChange(values.filter((v) => v !== value));
  }

  return (
    <div className="flex flex-wrap gap-1">
      {options.map((o) => {
        const selected = values.includes(o.value);
        return (
          <button
            key={o.value}
            type="button"
            onClick={() => {
              toggle(o.value);
            }}
            className={chipClass(selected)}
          >
            {o.label}
          </button>
        );
      })}
      {extras.map((v) => (
        <button
          key={`extra:${v}`}
          type="button"
          onClick={() => {
            removeExtra(v);
          }}
          className={`${chipClass(true)} inline-flex items-center gap-1`}
          title="Not a recognized value — click to remove"
        >
          {v}
          <X className="h-3 w-3" />
        </button>
      ))}
    </div>
  );
}

/**
 * An ordered pick-to-add control over a FIXED option set where order DOES matter (the gender-order
 * priority ranking): a `<select>` offers only the not-yet-added options (via {@link availableOptions}),
 * and the chosen values render as ↑↓-reorderable, removable chips in priority order. Stores the option
 * VALUES in user order. Mirrors {@link TagListInput}'s ordered reorder/remove, but the add path is a
 * constrained dropdown rather than free text — so only valid enum names can ever be added.
 */
export function OrderedPickToAdd({
  options,
  values,
  onChange,
  addPrompt,
}: {
  options: readonly ValueOption[];
  values: string[];
  onChange: (values: string[]) => void;
  addPrompt: string;
}) {
  const labelOf = (value: string) => options.find((o) => o.value === value)?.label ?? value;
  const offerable = availableOptions(options, values);

  function move(i: number, dir: -1 | 1) {
    const j = i + dir;
    if (j < 0 || j >= values.length) return;
    const next = [...values];
    [next[i], next[j]] = [next[j], next[i]];
    onChange(next);
  }
  function remove(i: number) {
    onChange(values.filter((_, idx) => idx !== i));
  }

  return (
    <div>
      {values.length > 0 ? (
        <div className="mb-1 flex flex-wrap gap-1">
          {values.map((v, i) => (
            <span
              key={v}
              className="inline-flex items-center gap-1 rounded-lg border border-border bg-card px-2 py-0.5 text-xs text-foreground"
            >
              <button
                type="button"
                aria-label={`Move ${labelOf(v)} up`}
                onClick={() => {
                  move(i, -1);
                }}
                className="text-muted hover:text-foreground"
              >
                ↑
              </button>
              <button
                type="button"
                aria-label={`Move ${labelOf(v)} down`}
                onClick={() => {
                  move(i, 1);
                }}
                className="text-muted hover:text-foreground"
              >
                ↓
              </button>
              <span>{labelOf(v)}</span>
              <button
                type="button"
                aria-label={`Remove ${labelOf(v)}`}
                onClick={() => {
                  remove(i);
                }}
                className="text-muted hover:text-foreground"
              >
                <X className="h-3 w-3" />
              </button>
            </span>
          ))}
        </div>
      ) : null}
      {offerable.length > 0 ? (
        <select
          // A select with no committed value: it returns to the prompt after each add (the value prop
          // stays the empty sentinel), so it always reads "Add a …" rather than the last pick.
          value=""
          onChange={(e) => {
            const v = e.target.value;
            if (v !== "") onChange([...values, v]);
          }}
          className={INPUT_CLASS}
        >
          <option value="">{addPrompt}</option>
          {offerable.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
      ) : null}
    </div>
  );
}

/**
 * A bespoke "Add token" affordance opening a `flex flex-wrap gap-1` click-to-add chip menu
 * of bare token names (NOT a dropdown, NOT autocomplete). It is purely ADDITIVE UI around a
 * {@link TagListInput} (it does not replace it). The host has no equivalent, so this is bespoke;
 * it reuses the selectable chip class verbatim (host-compiled classes only — no arbitrary
 * `[…]` values). Clicking a chip calls `onAdd(name)`; tokens already in `values` render
 * de-emphasized (existing `text-muted`) and skip the callback, mirroring TagListInput.addFrom de-dupe.
 * Every chip label is a React text node (auto-escaped, so token names can't inject markup).
 */
export function TokenPicker({
  tokens,
  values,
  onAdd,
}: {
  tokens: readonly string[];
  values: string[];
  onAdd: (name: string) => void;
}) {
  return (
    <div className="mt-1">
      <span className="mb-1 block text-xs text-muted">Add a token:</span>
      <div className="flex flex-wrap gap-1">
        {tokens.map((name) => {
          const present = values.includes(name);
          return (
            <button
              key={name}
              type="button"
              disabled={present}
              onClick={() => {
                onAdd(name);
              }}
              className={
                present
                  ? `${CHIP_BASE} border-border bg-card text-muted font-mono`
                  : `${chipClass(false)} font-mono`
              }
            >
              {name}
            </button>
          );
        })}
      </div>
    </div>
  );
}

/**
 * Generic add/remove/reorder editor for a list of typed-object rows. Controlled (`rows`/`onChange`,
 * no internal persistence). The caller supplies the row layout via `renderRow` and a fresh blank row
 * via `makeRow`, so routing (Pattern+IsRegex+Dest), excludes (Pattern+IsRegex), and field-replacers
 * (TargetToken+Find+Replace) all reuse this one editor without forking — the render-prop shape is the
 * only thing that keeps the generic worth its weight over three near-duplicate row components.
 *
 * `renderRow` receives the row, its index, and a typed patch callback so a field edit produces a fresh
 * row object (the update never mutates `rows` in place — it rebuilds the array and calls `onChange`).
 * Reorder (`ordered`) mirrors {@link TagListInput}'s ↑↓ move; empty state renders only the add control.
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
  // A stable key per row, carried in lockstep with the data ops. The rows are arbitrary typed
  // objects with no id, and an edit rebuilds the row object — so neither the array index nor the
  // object reference is a stable React key (an index key reattaches a removed/reordered row's DOM +
  // uncommitted input state to the wrong row). The key list lives in a ref so reorder/remove/add can
  // keep it in step without a state-sync render: the ref is written only from event handlers, and
  // render only READS it (writing a ref during render is what react-compiler forbids, not reading).
  // A stable key per row. The rows are arbitrary typed objects with no id, and an edit rebuilds the
  // row object — so neither the array index nor the object reference is a stable React key (an index
  // key reattaches a removed/reordered row's DOM to the wrong row, which would corrupt a row that
  // holds focus or an uncommitted transition). These counter-backed keys move with the data ops:
  // remove/reorder permute them, add mints a fresh one, and an external wholesale replacement
  // (load/Reset) re-seeds via the length-drift effect below.
  const [keys, setKeys] = useState<number[]>(() => rows.map((_, i) => i));
  const nextKey = useRef(rows.length);
  // Re-seed when `rows` is replaced wholesale from outside (lengths diverge); the index fallback in
  // the key prop covers the single frame before this lands. Syncing external props into local state
  // is the sanctioned use of the disable here (same pattern as SegmentedReplace's mode sync).
  // The guard makes this a no-op on an edit (row count unchanged), so depending on the whole `rows`
  // array re-runs the effect harmlessly per edit and only re-seeds on a genuine length change.
  /* eslint-disable react-hooks/set-state-in-effect */
  useEffect(() => {
    if (keys.length !== rows.length) {
      nextKey.current = rows.length;
      setKeys(rows.map((_, i) => i));
    }
  }, [rows, keys.length]);
  /* eslint-enable react-hooks/set-state-in-effect */

  function update(i: number, patch: Partial<T>) {
    onChange(rows.map((row, idx) => (idx === i ? { ...row, ...patch } : row)));
  }

  function remove(i: number) {
    onChange(rows.filter((_, idx) => idx !== i));
    setKeys((ks) => ks.filter((_, idx) => idx !== i));
  }

  function move(i: number, dir: -1 | 1) {
    const j = i + dir;
    if (j < 0 || j >= rows.length) return;
    const next = [...rows];
    [next[i], next[j]] = [next[j], next[i]];
    onChange(next);
    setKeys((ks) => {
      const nk = [...ks];
      [nk[i], nk[j]] = [nk[j], nk[i]];
      return nk;
    });
  }

  function add() {
    onChange([...rows, makeRow()]);
    setKeys((ks) => [...ks, nextKey.current++]);
  }

  return (
    <div className="space-y-2">
      {rows.map((row, i) => (
        <div
          key={keys.length === rows.length ? keys[i] : i}
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
      <GhostButton onClick={add}>{addLabel}</GhostButton>
    </div>
  );
}

/**
 * Generic key→value map editor. Controlled (`map`/`onChange`, no internal persistence). Renders one
 * row per existing entry and a separate "add" row; the caller renders both the key and value controls
 * via render-props (`renderKey`/`renderValue`) so the studio/tag picker can supply a searchable key
 * control while a plain text map supplies a text input — the map editor itself stays agnostic to how a
 * key is chosen.
 *
 * De-dupe on add mirrors {@link TagListInput}: adding a key already present is refused rather than
 * silently overwriting the existing value (a silent overwrite would lose the prior mapping). The
 * pending key/value are local draft state; only a successful add commits them through `onChange`.
 * Keys render as React text nodes (auto-escaped).
 */
export function KeyValueMapEditor({
  map,
  onChange,
  renderKey,
  renderValue,
  renderKeyLabel,
  addLabel,
}: {
  map: Record<string, string>;
  onChange: (map: Record<string, string>) => void;
  renderKey: (
    draftKey: string,
    setDraftKey: (key: string) => void,
    existingKeys: readonly string[],
  ) => ReactNode;
  renderValue: (value: string, setValue: (value: string) => void) => ReactNode;
  // How a committed row's key displays. Defaults to the raw key; an opaque-id key (e.g. a studio id)
  // supplies this to show a human label so a saved rule reads "Studio Name → …" not "42 → …".
  renderKeyLabel?: (key: string) => string;
  addLabel: string;
}) {
  const [draftKey, setDraftKey] = useState("");
  const [draftValue, setDraftValue] = useState("");
  const keys = Object.keys(map);

  function setValue(key: string, value: string) {
    onChange({ ...map, [key]: value });
  }

  function remove(key: string) {
    onChange(Object.fromEntries(Object.entries(map).filter(([k]) => k !== key)));
  }

  function add() {
    const k = draftKey.trim();
    // Refuse a blank or duplicate key — a duplicate add must not clobber the existing mapping.
    if (k.length === 0 || k in map) return;
    onChange({ ...map, [k]: draftValue });
    setDraftKey("");
    setDraftValue("");
  }

  const duplicate = draftKey.trim().length > 0 && draftKey.trim() in map;

  return (
    <div className="space-y-2">
      {keys.map((key) => (
        <div
          key={key}
          className="flex items-center gap-2 rounded-xl border border-border bg-card p-3"
        >
          <span className="min-w-0 flex-1 truncate font-mono text-sm text-foreground">
            {renderKeyLabel ? renderKeyLabel(key) : key}
          </span>
          <span className="flex-1">
            {renderValue(map[key], (v) => {
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
        <span className="min-w-0 flex-1">{renderKey(draftKey, setDraftKey, keys)}</span>
        <span className="flex-1">{renderValue(draftValue, setDraftValue)}</span>
        <GhostButton onClick={add} disabled={draftKey.trim().length === 0 || duplicate}>
          {addLabel}
        </GhostButton>
      </div>
      {duplicate ? <StatusText kind="error">That key already has a value.</StatusText> : null}
    </div>
  );
}

/**
 * Inline regex-validity message for a rule row. Presentational only — it shows an error when the
 * pattern is in regex mode and obviously malformed, and renders nothing otherwise; the consuming row
 * decides whether to block on it. The verdict comes from the tested {@link isRegexValid}, whose
 * browser-vs-.NET caveat applies: this flags obvious JS parse errors, not full .NET parity, so a
 * clean result is not a promise the engine will accept the pattern.
 */
export function RegexValidity({ pattern, isRegex }: { pattern: string; isRegex: boolean }) {
  if (!isRegex) return null;
  const result = isRegexValid(pattern);
  if (result.valid) return null;
  return <StatusText kind="error">Invalid pattern: {result.message}</StatusText>;
}

/**
 * Advisory-only, non-blocking hint that a destination-path field doesn't look like an absolute path.
 * Mirrors {@link RegexValidity}'s presentational shape exactly: pure, stateless, renders nothing when
 * the value looks fine or is blank (blank already means "no route" for every field this wraps).
 */
export function PathShapeHint({ value }: { value: string }) {
  if (value.trim().length === 0) return null;
  if (isAbsolutePathShape(value)) return null;
  return <StatusText kind="warning">Doesn't look like an absolute path.</StatusText>;
}

/** One of the five field groups. Matches Cove sub-card styling. */
export function GroupCard({
  title,
  description,
  headerRight,
  children,
}: {
  title: string;
  description?: string;
  headerRight?: ReactNode;
  children: ReactNode;
}) {
  return (
    <div className="rounded-xl border border-border bg-card p-4">
      {headerRight ? (
        <div className="flex items-center justify-between gap-4">
          <h3 className="text-base font-semibold text-foreground">{title}</h3>
          {headerRight}
        </div>
      ) : (
        <h3 className="text-base font-semibold text-foreground">{title}</h3>
      )}
      {description ? (
        <p className="mb-4 mt-1 text-sm text-secondary">{description}</p>
      ) : (
        <div className="mb-4" />
      )}
      <div className="space-y-4">{children}</div>
    </div>
  );
}

/**
 * Progressive-disclosure section (mirrors Cove's native `CollapsibleSection`, which isn't importable
 * from an extension bundle). Collapsed by default; the header always shows the title + a one-line
 * `summary` of what's inside so a closed section still telegraphs its contents (NN/g accordion rule).
 * Self-manages open state. Host-compiled classes only.
 */
export function CollapsibleSection({
  title,
  summary,
  defaultOpen = false,
  children,
}: {
  title: string;
  summary?: string;
  defaultOpen?: boolean;
  children: ReactNode;
}) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <div className="overflow-hidden rounded-xl border border-border">
      <button
        type="button"
        onClick={() => {
          setOpen((v) => !v);
        }}
        aria-expanded={open}
        className="flex w-full items-center justify-between gap-4 bg-card px-4 py-3 text-left transition-colors hover:bg-card-hover"
      >
        <span className="min-w-0">
          <span className="block text-sm font-medium text-foreground">{title}</span>
          {summary ? (
            <span className="mt-1 block truncate text-xs text-muted">{summary}</span>
          ) : null}
        </span>
        {open ? (
          <ChevronUp className="h-4 w-4 shrink-0 text-muted" />
        ) : (
          <ChevronDown className="h-4 w-4 shrink-0 text-muted" />
        )}
      </button>
      {open ? <div className="space-y-4 border-t border-border px-4 py-4">{children}</div> : null}
    </div>
  );
}

export function PrimaryButton({
  children,
  onClick,
  disabled,
}: {
  children: ReactNode;
  onClick: () => void;
  disabled?: boolean;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      className="inline-flex items-center gap-2 rounded-lg bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-60"
    >
      {children}
    </button>
  );
}

export function GhostButton({
  children,
  onClick,
  disabled,
}: {
  children: ReactNode;
  onClick: () => void;
  disabled?: boolean;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      className="inline-flex items-center gap-1.5 rounded-lg border border-border bg-card px-3 py-2 text-sm font-medium text-secondary hover:border-accent/50 hover:bg-card-hover hover:text-foreground disabled:opacity-60"
    >
      {children}
    </button>
  );
}

export type StatusKind = "success" | "error" | "muted" | "warning";

export function StatusText({ kind, children }: { kind: StatusKind; children: ReactNode }) {
  const cls =
    kind === "success"
      ? "text-green-400"
      : kind === "error"
        ? "text-red-400"
        : kind === "warning"
          ? "text-amber-400"
          : "text-secondary";
  return <span className={`text-xs ${cls}`}>{children}</span>;
}

export function Spinner() {
  return <Loader2 className="h-4 w-4 animate-spin" />;
}
