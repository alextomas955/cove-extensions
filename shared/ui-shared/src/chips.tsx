/**
 * The chip vocabulary and the controls built from it: quick-pick rows, the two-option segmented
 * control, the chips-plus-input tag editor, and the two fixed-option pickers.
 *
 * A chip is a `button`, which is labelable, so none of these may sit under a `<label>`. They take
 * {@link FieldGroup} instead, and each control that owns an input names it with `ariaLabel`.
 */
import { X } from "lucide-react";
import type { ReactNode } from "react";
import { useId, useRef, useState } from "react";

import {
  availableOptions,
  listEditors,
  nextActiveIndex,
  suggestionOptions,
  type ValueOption,
} from "./primitivesLogic";
import { INPUT_CLASS, TextInput } from "./fields";

/**
 * Selectable chip styling. Host-compiled classes only - no arbitrary `[…]` values, because the
 * host's Tailwind JIT never scans this bundle.
 *
 * A color-free shape base plus two mutually exclusive color sets. Tailwind resolves two utilities
 * targeting the same property by their order in the generated stylesheet, not by the order in the
 * class attribute, and the host emits `.bg-card` / `.text-foreground` / `.border-border` after
 * `.bg-accent` / `.text-accent` / `.border-accent`. A selected state built by appending accent
 * utilities to a base carrying the card ones therefore loses every color conflict and renders as
 * unselected.
 */
const CHIP_BASE = "cursor-pointer rounded-lg border px-2 py-1 text-xs";

const CHIP_UNSELECTED =
  "border-border bg-card text-foreground hover:border-accent/50 hover:text-accent";

// Cove's own chip selected state is an accent tint, not the solid `bg-accent text-white` it uses
// for segmented toggles. The tint carries selection on the background, a property the unselected
// set's text and border utilities do not contest, so source order cannot hide it.
const CHIP_SELECTED = "border-accent bg-accent/15 text-foreground";

/** The full chip class for a selected/unselected chip - one color set, never both. */
function chipClass(selected: boolean): string {
  return `${CHIP_BASE} ${selected ? CHIP_SELECTED : CHIP_UNSELECTED}`;
}

export function Chip({
  selected,
  toggle = false,
  onClick,
  disabled,
  title,
  mono,
  children,
}: {
  selected: boolean;
  // A chip that picks a value reports its state to assistive technology. An action chip, which
  // inserts or applies something, is a plain button even when it is tinted.
  toggle?: boolean;
  onClick: () => void;
  disabled?: boolean;
  title?: string;
  mono?: boolean;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      title={title}
      aria-pressed={toggle ? selected : undefined}
      className={mono ? `${chipClass(selected)} font-mono` : chipClass(selected)}
    >
      {children}
    </button>
  );
}

/** The two entry-identity rules {@link TagListInput} chooses between. */
const identity = (value: string) => value;

const fold = (value: string) => value.toLowerCase();

export interface SeparatorOption {
  /** The separator as persisted, for example `", "`. */
  value: string;
  /** The label, with any leading or trailing whitespace made visible so no chip reads as empty. */
  label: string;
}

/**
 * A quick-pick chip row of known separators plus a `Custom` chip, which reveals a mono
 * {@link TextInput} pre-filled with the saved value whenever that value matches no preset.
 */
export function SeparatorChips({
  value,
  onChange,
  options,
  customPlaceholder,
  ariaLabel,
}: {
  value: string;
  onChange: (value: string) => void;
  options: readonly SeparatorOption[];
  customPlaceholder?: string;
  // Names the custom input this row reveals; the heading above the row names the row, not the input.
  ariaLabel?: string;
}) {
  const isCustom = !options.some((o) => o.value === value);

  return (
    <div>
      <div className="flex flex-wrap gap-1">
        {options.map((o) => {
          const selected = o.value === value;
          return (
            <Chip
              key={o.value || "__empty__"}
              selected={selected}
              toggle
              onClick={() => {
                onChange(o.value);
              }}
            >
              {o.label}
            </Chip>
          );
        })}
        <Chip
          selected={isCustom}
          toggle
          onClick={() => {
            if (!isCustom) onChange("");
          }}
        >
          Custom
        </Chip>
      </div>
      {isCustom ? (
        <div className="mt-2">
          <TextInput
            value={value}
            onChange={onChange}
            placeholder={customPlaceholder}
            ariaLabel={ariaLabel}
            mono
          />
        </div>
      ) : null}
    </div>
  );
}

/**
 * A two-option segmented control. The strip option binds `""`; the replace option reveals a mono
 * {@link TextInput} bound to the value, and focuses it when the value is empty.
 */
export function SegmentedReplace({
  value,
  onChange,
  stripLabel,
  replaceLabel,
  stripHelper,
  replaceHelper,
  inputPlaceholder,
  ariaLabel,
}: {
  value: string;
  onChange: (value: string) => void;
  stripLabel: string;
  replaceLabel: string;
  stripHelper?: string;
  replaceHelper?: string;
  inputPlaceholder?: string;
  // Names the replacement input this control reveals; the heading above it names the whole control.
  ariaLabel?: string;
}) {
  const inputRef = useRef<HTMLInputElement>(null);
  // Replace is a mode of its own, not `value !== ""`: choosing it on an empty value has to reveal
  // the input, and clearing the field mid-edit must not collapse the control under the caret. Only a
  // change to "" this control did not make - a load, a reset - returns it to strip mode.
  const [mode, setMode] = useState(() => ({ seen: value, replace: value !== "" }));
  const current = mode.seen === value ? mode : { seen: value, replace: value !== "" };
  if (current !== mode) setMode(current);
  const replaceActive = current.replace || value !== "";

  function chooseStrip() {
    setMode({ seen: "", replace: false });
    if (value !== "") onChange("");
  }
  function chooseReplace() {
    setMode({ seen: value, replace: true });
    // The input is revealed by this render, so the focus waits for it to exist.
    requestAnimationFrame(() => inputRef.current?.focus());
  }
  // Recording what the field produced is the only way an empty field under the user's caret is told
  // from a value emptied elsewhere: both reach this control as the same prop change.
  function edit(next: string) {
    setMode({ seen: next, replace: true });
    onChange(next);
  }

  return (
    <div>
      <div className="flex gap-1">
        <Chip selected={!replaceActive} toggle onClick={chooseStrip}>
          {stripLabel}
        </Chip>
        <Chip selected={replaceActive} toggle onClick={chooseReplace}>
          {replaceLabel}
        </Chip>
      </div>
      {replaceActive ? (
        <div className="mt-2">
          <TextInput
            value={value}
            onChange={edit}
            placeholder={inputPlaceholder}
            inputRef={inputRef}
            ariaLabel={ariaLabel}
            mono
          />
          {replaceHelper ? (
            <span className="mt-1 block text-xs text-secondary">{replaceHelper}</span>
          ) : null}
        </div>
      ) : null}
      {!replaceActive && stripHelper ? (
        <span className="mt-1 block text-xs text-secondary">{stripHelper}</span>
      ) : null}
    </div>
  );
}

/**
 * One entry of a list editor: a removable chip, carrying the ↑↓ pair where the list's order is the
 * user's. `name` is both what the chip reads and what its buttons are named after - the value
 * itself in a free-text list, the option's label where the stored value is an opaque id - so a
 * screen reader never announces three unlabelled buttons in a row.
 */
function ListChip({
  name,
  mono = false,
  onMove,
  onRemove,
}: {
  name: string;
  mono?: boolean;
  /** Omitted where the list has no order to change, which drops the ↑↓ pair. */
  onMove?: (dir: -1 | 1) => void;
  onRemove: () => void;
}) {
  return (
    <span className="inline-flex items-center gap-1 rounded-lg border border-border bg-card px-2 py-0.5 text-xs text-foreground">
      {onMove ? (
        <>
          <button
            type="button"
            aria-label={`Move ${name} up`}
            onClick={() => {
              onMove(-1);
            }}
            className="text-muted hover:text-foreground"
          >
            ↑
          </button>
          <button
            type="button"
            aria-label={`Move ${name} down`}
            onClick={() => {
              onMove(1);
            }}
            className="text-muted hover:text-foreground"
          >
            ↓
          </button>
        </>
      ) : null}
      <span className={mono ? "font-mono" : undefined}>{name}</span>
      <button
        type="button"
        aria-label={`Remove ${name}`}
        onClick={onRemove}
        className="text-muted hover:text-foreground"
      >
        <X className="h-3 w-3" />
      </button>
    </span>
  );
}

/**
 * A string-list editor: removable chips above an add-on-Enter input, with ↑↓ reordering under
 * `ordered`.
 *
 * `suggestions` turns the input into a combobox over that set. The set is never closed: free text
 * still commits, because a value outside the set is a legitimate entry the caller warns about
 * rather than refuses.
 */
export function TagListInput({
  values,
  onChange,
  placeholder,
  ordered = false,
  normalize,
  onReject,
  onLiveChange,
  suggestions,
  ariaLabel,
}: {
  values: string[];
  onChange: (values: string[]) => void;
  placeholder?: string;
  ordered?: boolean;
  normalize?: (raw: string) => string;
  onReject?: (candidate: string) => boolean;
  onLiveChange?: (raw: string) => void;
  suggestions?: readonly string[];
  ariaLabel?: string;
}) {
  const id = useId();
  const listId = `${id}-suggestions`;
  // When two entries are the same entry. A suggestion set is a vocabulary its consumer resolves
  // case-insensitively, so two spellings of one token are one token and a list holding both acts on
  // it twice. A free-text list keeps exact match, where case is the user's own.
  const sameEntry = suggestions === undefined ? identity : fold;
  const [query, setQuery] = useState("");
  const [focused, setFocused] = useState(false);
  // Separate from focus, so Escape can put the list away without taking the caret out of the field.
  const [dismissed, setDismissed] = useState(false);
  const [active, setActive] = useState(-1);

  function clearInput() {
    setQuery("");
    setActive(-1);
    onLiveChange?.("");
  }

  function add(candidate: string) {
    const taken = values.some((existing) => sameEntry(existing) === sameEntry(candidate));
    if (!taken) onChange([...values, candidate]);
    clearInput();
  }

  function addCurrent() {
    const v = (normalize ? normalize(query) : query).trim();
    // Text the control will not take stays on screen for the user to correct.
    if (v.length === 0) return;
    if (onReject?.(v)) return;
    add(v);
  }

  function commit(option: string) {
    add(option);
  }

  const offered = suggestions ? suggestionOptions(suggestions, values, query, sameEntry) : [];
  const listOpen = suggestions !== undefined && focused && !dismissed && offered.length > 0;
  const activeId =
    listOpen && active >= 0 && active < offered.length ? `${listId}-${active}` : undefined;

  const { move, remove } = listEditors(values, onChange);

  return (
    <div>
      {values.length > 0 ? (
        <div className="mb-1 flex flex-wrap gap-1">
          {values.map((v, i) => (
            <ListChip
              key={`${v}-${i}`}
              name={v}
              mono
              onMove={
                ordered
                  ? (dir) => {
                      move(i, dir);
                    }
                  : undefined
              }
              onRemove={() => {
                remove(i);
              }}
            />
          ))}
        </div>
      ) : null}
      <div className="relative">
        <input
          id={id}
          type="text"
          value={query}
          placeholder={placeholder}
          className={INPUT_CLASS}
          aria-label={ariaLabel}
          role={suggestions ? "combobox" : undefined}
          aria-autocomplete={suggestions ? "list" : undefined}
          aria-expanded={suggestions ? listOpen : undefined}
          aria-controls={suggestions ? listId : undefined}
          aria-activedescendant={activeId}
          onChange={(e) => {
            setQuery(e.target.value);
            setActive(-1);
            setDismissed(false);
            onLiveChange?.(e.target.value);
          }}
          onFocus={() => {
            setFocused(true);
            setDismissed(false);
          }}
          onKeyDown={(e) => {
            if (listOpen && (e.key === "ArrowDown" || e.key === "ArrowUp")) {
              e.preventDefault();
              setActive(nextActiveIndex(active, offered.length, e.key === "ArrowDown" ? 1 : -1));
              return;
            }
            if (e.key === "Escape" && listOpen) {
              e.preventDefault();
              setDismissed(true);
              return;
            }
            if (e.key === "Enter") {
              e.preventDefault();
              const chosen = listOpen && active >= 0 ? offered[active] : undefined;
              if (chosen === undefined) addCurrent();
              else commit(chosen);
            }
          }}
          onBlur={() => {
            setFocused(false);
            addCurrent();
          }}
        />
        {listOpen ? (
          <ul
            id={listId}
            role="listbox"
            className="absolute left-0 right-0 top-full z-20 mt-1 max-h-48 list-none overflow-auto rounded-xl border border-border bg-card py-1 shadow-lg"
            // The blur handler commits whatever the input holds, so without this a click would first
            // blur (adding the half-typed query) and then add the option too.
            onMouseDown={(e) => {
              e.preventDefault();
            }}
          >
            {offered.map((option, i) => (
              // The input owns the keyboard: it carries role="combobox" and handles ArrowDown,
              // ArrowUp, Enter and Escape. An option has no tabIndex, so it never takes focus and a
              // key handler on it could not fire. Hence the suppression rather than a listener.
              <li // NOSONAR
                key={option}
                id={`${listId}-${i}`}
                role="option"
                aria-selected={i === active}
                onClick={() => {
                  commit(option);
                }}
                className={
                  i === active
                    ? "cursor-pointer px-3 py-1 font-mono text-sm text-foreground bg-card-hover"
                    : "cursor-pointer px-3 py-1 font-mono text-sm text-foreground hover:bg-card-hover"
                }
              >
                {option}
              </li>
            ))}
          </ul>
        ) : null}
      </div>
    </div>
  );
}

/**
 * A toggle-chip multiselect over a fixed option set where order does not matter. Stores the option
 * values in the option set's order rather than click order, so two configurations with the same
 * members serialize identically.
 *
 * A stored value not in `options` is preserved rather than dropped: it renders as an extra removable
 * chip, and toggling a known option keeps it. Otherwise the first toggle would erase a value the
 * user cannot see.
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
  // Stored values outside the offered set - kept verbatim so a toggle never erases them.
  const extras = values.filter((v) => !optionValues.has(v));

  function toggle(value: string) {
    const has = values.includes(value);
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
          <Chip
            key={o.value}
            selected={selected}
            toggle
            onClick={() => {
              toggle(o.value);
            }}
          >
            {o.label}
          </Chip>
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
          title="Not a recognized value - click to remove"
        >
          {v}
          <X className="h-3 w-3" />
        </button>
      ))}
    </div>
  );
}

/**
 * An ordered pick-to-add control over a fixed option set. The `<select>` offers only the not-yet
 * added options, so only a value from the set can ever be added, and the chosen values render as
 * ↑↓-reorderable removable chips in the user's order.
 */
export function OrderedPickToAdd({
  options,
  values,
  onChange,
  addPrompt,
  ariaLabel,
}: {
  options: readonly ValueOption[];
  values: string[];
  onChange: (values: string[]) => void;
  addPrompt: string;
  // Names the add select; the heading above this control names the chips and the select together.
  ariaLabel?: string;
}) {
  const labelOf = (value: string) => options.find((o) => o.value === value)?.label ?? value;
  const offerable = availableOptions(options, values);

  const { move, remove } = listEditors(values, onChange);

  return (
    <div>
      {values.length > 0 ? (
        <div className="mb-1 flex flex-wrap gap-1">
          {values.map((v, i) => (
            <ListChip
              key={v}
              name={labelOf(v)}
              onMove={(dir) => {
                move(i, dir);
              }}
              onRemove={() => {
                remove(i);
              }}
            />
          ))}
        </div>
      ) : null}
      {offerable.length > 0 ? (
        <select
          // The value prop stays the empty sentinel, so the select returns to the prompt after each
          // add rather than reading back the last pick.
          value=""
          aria-label={ariaLabel}
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
