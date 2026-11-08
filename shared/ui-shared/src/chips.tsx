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

import { availableOptions, type ValueOption } from "./entityPickerLogic";
import { INPUT_CLASS, TextInput } from "./fields";
import { listEditors, nextActiveIndex, suggestionOptions } from "./primitivesLogic";

/**
 * Selectable chip/button styling, matching the TokenLegend / PresetRow chip. Host-compiled classes
 * only - no arbitrary `[…]` values, because the host's Tailwind JIT never scans this bundle.
 *
 * The selected and unselected states must not share a conflicting color utility. Tailwind resolves
 * two utilities targeting the same property by their order in the generated stylesheet, not by the
 * order in the class attribute - and the host emits `.bg-card` / `.text-foreground` / `.border-border`
 * after `.bg-accent` / `.text-accent` / `.border-accent`. So a selected state built by appending accent
 * utilities to the base chip (which carries the card/foreground/border ones) loses every color
 * conflict and renders identically to unselected - the bug that made the selection invisible.
 *
 * The fix: a color-free shape base, plus two mutually exclusive color sets. {@link chipClass} picks one
 * - never both - so no same-property conflict exists and the host's source order is irrelevant.
 */
const CHIP_BASE = "cursor-pointer rounded-lg border px-2 py-1 text-xs";

const CHIP_UNSELECTED =
  "border-border bg-card text-foreground hover:border-accent/50 hover:text-accent";

// Cove's own chip/pill selected state is an accent tint, not a solid fill - `border-accent bg-accent/15`
// (see CustomFields enum chips / BookmarkButton). The solid `bg-accent text-white` is Cove's idiom for
// segmented toggle buttons, not chips, and reads too heavy here. The tint still carries selection on the
// background (a property the unselected set's text/border utilities don't contest), so it stays visible
// regardless of the host stylesheet's source order - the conflict that hid an earlier border/text tint.
const CHIP_SELECTED = "border-accent bg-accent/15 text-foreground";

/** The full chip class for a selected/unselected chip - one color set, never both. */
function chipClass(selected: boolean): string {
  return `${CHIP_BASE} ${selected ? CHIP_SELECTED : CHIP_UNSELECTED}`;
}

/** Shared selectable-chip button, wrapping {@link chipClass} so every chip site renders identically. */
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
 * matches no preset. Binds the same separator string via `onChange` - no shape change.
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
            // Selecting Custom from a preset clears to a fresh custom entry; if already custom, keep.
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
  // "Replace with" is a mode of its own, not `value !== ""`. Choosing it on an empty value has to
  // reveal the input or there is nowhere to type, and clearing the field mid-edit must not collapse
  // the control under the caret. Only a change to "" this control did not make - a load, a reset -
  // returns it to strip mode, which is what the value the last render saw distinguishes.
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
  // Typing in the field records what it produced, which is the only way an empty field under the
  // user's caret is told from a value emptied from elsewhere: both reach this control as the same
  // prop change.
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
 * String-list editor: chips above an add-on-Enter input. Used for Whitelist / Blacklist /
 * RequiredFields / DropOrder. DropOrder additionally gets up/down reordering (`ordered`).
 *
 * `suggestions` turns the same input into a combobox offering that set as a filtered list. The set
 * is never closed: free text still commits, because a value outside the set is a legitimate entry
 * the caller warns about rather than refuses.
 */
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
  // When two entries are the same entry. A suggestion set is a vocabulary the rename engine resolves
  // case-insensitively (`Engine/TemplateEngine.cs` builds its token maps with
  // `StringComparer.OrdinalIgnoreCase`), so two spellings of one token are one token to it: a
  // drop-order list holding both makes `Engine/LengthReducer.cs` re-render for each and report the
  // same field dropped twice. Free-text lists keep exact match, where case is the user's own.
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
              // key handler on it could not fire, hence the suppression rather than a missing listener.
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
 * A toggle-chip multiselect over a fixed option set where order does not matter (the ignore-genders
 * list): clicking a chip toggles its membership, selected chips carry the accent tint via
 * {@link chipClass}. Stores the option values (e.g. the gender enum names), in the option set's order
 * rather than click order - so two configs with the same members serialize identically. Every label
 * is a React text node (auto-escaped).
 *
 * A stored value not in `options` (e.g. one saved via the old free-text control, or a gender this
 * build doesn't list) is preserved, not silently dropped: it renders as an extra removable chip and
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
  // Stored values outside the offered set - kept verbatim so a toggle never erases them.
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
 * An ordered pick-to-add control over a fixed option set where order does matter (the gender-order
 * priority ranking): a `<select>` offers only the not-yet-added options (via {@link availableOptions}),
 * and the chosen values render as ↑↓-reorderable, removable chips in priority order. Stores the option
 * values in user order. Mirrors {@link TagListInput}'s ordered reorder/remove, but the add path is a
 * constrained dropdown rather than free text - so only valid enum names can ever be added.
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
          // A select with no committed value: it returns to the prompt after each add (the value prop
          // stays the empty sentinel), so it always reads "Add a …" rather than the last pick.
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
