/**
 * Label-and-control primitives, kept local because the host set a settings panel can reach is
 * thinner than this one needs. `@cove/runtime/components` does export `Field`, `TextInput`,
 * `TextArea`, `NumberInput` and `SelectInput`, so check them before adding anything here. What they
 * do not cover:
 *
 * - Host `Field` takes `children: ReactNode` and pairs its label by nesting. A `<label>` forwards a
 *   click to its first labelable descendant, so over a chip row it flips whichever chip is drawn
 *   first. {@link Field} hands the control an id instead, and {@link FieldGroup} takes the blocks a
 *   label must not wrap at all.
 * - Host `TextInput` takes no `id`, `aria-label`, ref, `onFocus` or mono variant.
 * - Host `NumberInput` does not clamp to min/max and has no blank-when-zero rendering.
 *
 * The host has no toggle and no button at all.
 *
 * Every class string in this module and its siblings matches Cove's own, so the panel reads as
 * native and every utility resolves against the host's emitted stylesheet: no CSS bundle ships.
 * Focus uses Cove's `focus:border-accent focus:outline-none`; the host stylesheet emits no
 * `focus-visible:ring-*` utilities.
 */
import type { ReactNode } from "react";
import { useId } from "react";

import { numberInputValue } from "./primitivesLogic";

/** Exported so an embedded host control can take the same string and match every input here. */
export const INPUT_CLASS =
  "w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none";

/** Sentinel `value` for the "Custom…" option in {@link ExampleSelect}. */
const CUSTOM_SENTINEL = "__custom__";

const MICRO_LABEL_CLASS = "mb-1 block text-xs font-medium uppercase tracking-wide text-muted";

// Names a group of controls, so it stays quieter than the section title containing it.
const GROUP_LABEL_CLASS = "mb-1 block text-sm text-secondary";

/**
 * Label + one control + optional helper. Matches Cove `SettingsField`.
 *
 * The label is paired to its control by an id this component owns and hands to `children`, so a
 * caller that does not put the id on a control fails to type-check. {@link FieldGroup} is what a
 * block of several controls takes.
 *
 * The helper sits outside the label, because a control takes its name from the whole text of the
 * label naming it and a helper inside one is read as part of that name. It reaches the control as a
 * description instead: `children` is handed its id, which a caller passes to `describedBy`. That
 * puts the label beside the control rather than around it, so `htmlFor` carries the pairing.
 */
export function Field({
  label,
  helper,
  labelStyle = "micro",
  children,
}: {
  label: string;
  helper?: string;
  labelStyle?: "micro" | "group";
  children: (controlId: string, describedById: string | undefined) => ReactNode;
}) {
  const controlId = useId();
  const helperId = useId();
  return (
    <div className="block text-sm">
      {label ? (
        <label
          htmlFor={controlId}
          className={labelStyle === "group" ? GROUP_LABEL_CLASS : MICRO_LABEL_CLASS}
        >
          {label}
        </label>
      ) : null}
      {children(controlId, helper ? helperId : undefined)}
      {helper ? (
        <span id={helperId} className="mt-1 block text-xs text-secondary">
          {helper}
        </span>
      ) : null}
    </div>
  );
}

/**
 * Heading + a set of controls + optional helper, laid out exactly as {@link Field} but headed by a
 * plain element rather than a label.
 *
 * For anything that is not one labelable control: a chip row, a segmented control, a chips-plus
 * input editor, the host's entity selector. A `<label>` forwards a click anywhere inside it to its
 * first labelable descendant, and `button` is labelable, so a label over any of those turns a click
 * on the heading into a click on whichever chip is drawn first - a setting silently changed, or a
 * configured value deleted. The heading names the block instead.
 *
 * A group name does not name a control nested inside it, so each control in here carries its own.
 */
export function FieldGroup({
  label,
  helper,
  labelStyle = "micro",
  children,
}: {
  label: string;
  helper?: string;
  labelStyle?: "micro" | "group";
  children: ReactNode;
}) {
  const labelId = useId();
  return (
    <fieldset
      className="block min-w-0 text-sm"
      title={helper}
      aria-labelledby={label ? labelId : undefined}
    >
      {label ? (
        <span
          id={labelId}
          className={labelStyle === "group" ? GROUP_LABEL_CLASS : MICRO_LABEL_CLASS}
        >
          {label}
        </span>
      ) : null}
      {children}
      {helper ? <span className="mt-1 block text-xs text-secondary">{helper}</span> : null}
    </fieldset>
  );
}

export function TextInput({
  value,
  onChange,
  onFocus,
  placeholder,
  mono = false,
  inputRef,
  ariaLabel,
  id,
  describedBy,
}: {
  value: string;
  onChange: (value: string) => void;
  onFocus?: () => void;
  placeholder?: string;
  mono?: boolean;
  inputRef?: React.Ref<HTMLInputElement>;
  // For an input inside a group rather than under a label of its own: a group's name does not reach it.
  ariaLabel?: string;
  id?: string;
  /** Id of the text describing this control, which {@link Field} hands to its caller. */
  describedBy?: string;
}) {
  return (
    <input
      ref={inputRef}
      id={id}
      type="text"
      value={value}
      placeholder={placeholder}
      aria-label={ariaLabel}
      aria-describedby={describedBy}
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
  max,
  placeholder,
  blankWhenZero,
  id,
  describedBy,
}: {
  value: number;
  onChange: (value: number) => void;
  min?: number;
  max?: number;
  placeholder?: string;
  /** Set where zero is the field's "unset", so it renders blank and the placeholder names it. */
  blankWhenZero?: boolean;
  id?: string;
  describedBy?: string;
}) {
  return (
    <input
      id={id}
      type="number"
      aria-describedby={describedBy}
      value={numberInputValue(value, blankWhenZero)}
      placeholder={placeholder}
      min={min}
      max={max}
      onChange={(e) => {
        if (e.target.value === "") {
          onChange(0);
          return;
        }
        let next = Number(e.target.value);
        if (max !== undefined && next > max) next = max;
        if (min !== undefined && next < min) next = min;
        onChange(next);
      }}
      className={`themed-number-input ${INPUT_CLASS}`}
    />
  );
}

export function Select<T extends string>({
  value,
  onChange,
  options,
  disabled = false,
  ariaLabel,
  id,
  describedBy,
}: {
  value: T;
  onChange: (value: T) => void;
  options: readonly { value: T; label: string }[];
  disabled?: boolean;
  // A native <select> has no visible label of its own; a caller with no adjacent label names it here.
  ariaLabel?: string;
  id?: string;
  describedBy?: string;
}) {
  return (
    <select
      id={id}
      aria-describedby={describedBy}
      value={value}
      disabled={disabled}
      aria-label={ariaLabel}
      onChange={(e) => {
        onChange(e.target.value as T);
      }}
      className={`${INPUT_CLASS} disabled:opacity-60`}
    >
      {options.map((o) => (
        <option key={o.value} value={o.value}>
          {o.label}
        </option>
      ))}
    </select>
  );
}

export interface ExampleOption {
  /** The format string as persisted, for example `"yyyy-MM-dd"`. */
  value: string;
  /** A static reference rendering of `value`, for example `"2026-03-12"`. */
  example: string;
}

/**
 * A native `<select>` of known format strings, each option folding a reference example into its
 * text, plus a `Custom…` sentinel revealing a mono {@link TextInput}.
 *
 * A value matching no known option auto-selects `Custom…` and shows the input pre-filled, so a
 * previously customised value is never lost. Only how the user produces the string changes; the
 * persisted value keeps its shape.
 */
export function ExampleSelect({
  value,
  onChange,
  options,
  customPlaceholder,
  ariaLabel,
}: {
  value: string;
  onChange: (value: string) => void;
  options: readonly ExampleOption[];
  customPlaceholder?: string;
  // Names the select and, when it is revealed, the custom input: this control is a block, so the
  // heading above it names the block and reaches neither.
  ariaLabel?: string;
}) {
  const matched = options.find((o) => o.value === value);
  const isCustom = matched === undefined;
  const selectValue = isCustom ? CUSTOM_SENTINEL : value;
  const helperExample = matched ? `${matched.value} → ${matched.example}` : value;

  return (
    <div>
      <select
        value={selectValue}
        aria-label={ariaLabel}
        onChange={(e) => {
          const v = e.target.value;
          // Choosing Custom… keeps an already-custom value and otherwise seeds empty, so the
          // revealed input starts blank.
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
          <TextInput
            value={value}
            onChange={onChange}
            placeholder={customPlaceholder}
            ariaLabel={ariaLabel}
            mono
          />
        </div>
      ) : (
        <span className="mt-1 block font-mono text-xs text-secondary">{helperExample}</span>
      )}
    </div>
  );
}

export function Toggle({
  label,
  checked,
  onChange,
  helper,
  ariaLabel,
}: {
  label: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
  helper?: string;
  ariaLabel?: string;
}) {
  const id = useId();
  return (
    <div>
      <label htmlFor={id} className="flex items-center gap-2 text-sm text-secondary" title={helper}>
        <button
          id={id}
          type="button"
          role="switch"
          aria-checked={checked}
          aria-label={label ? undefined : ariaLabel}
          onClick={() => {
            onChange(!checked);
          }}
          className={`inline-flex h-5 w-9 items-center rounded-full transition-colors ${
            checked ? "bg-accent" : "border border-border"
          }`}
          // The off track must step away from card and surface alike or it reads as a bare knob.
          // Inline, because the host's prebuilt stylesheet is the only source of classes here.
          style={checked ? undefined : { backgroundColor: "var(--color-border)" }}
        >
          <span
            className="inline-block h-4 w-4 rounded-full bg-white transition-transform"
            // An inline transform, not a translate-x-* utility. An extension ships no CSS of its
            // own and styles only through classes the host's prebuilt Tailwind bundle already
            // emits; Cove's own UI uses neither translate-x-4 nor translate-x-0.5, so the host
            // never compiles them and as classes they would resolve to nothing. An inline style is
            // element-scoped, so it cannot leak onto host pages.
            style={{
              transform: checked ? "translateX(1rem)" : "translateX(0.125rem)",
            }}
          />
        </button>
        {label ? <span>{label}</span> : null}
      </label>
      {helper ? <p className="mt-1 text-xs text-secondary">{helper}</p> : null}
    </div>
  );
}

export function Button({
  variant = "primary",
  children,
  onClick,
  disabled,
  describedBy,
}: {
  variant?: "primary" | "ghost";
  children: ReactNode;
  onClick: () => void;
  disabled?: boolean;
  /**
   * Id of the element saying why the button is disabled. Spelled without the hyphen so that a
   * caller naming it wrong is a type error: TypeScript skips excess-property checks on a
   * hyphenated JSX attribute, so an `aria-describedby` passed to a component that does not take
   * one compiles and is dropped.
   */
  describedBy?: string;
}) {
  const className =
    variant === "ghost"
      ? "inline-flex items-center gap-1.5 rounded-lg border border-border bg-card px-3 py-2 text-sm font-medium text-secondary hover:border-accent/50 hover:bg-card-hover hover:text-foreground disabled:opacity-60"
      : "inline-flex items-center gap-2 rounded-lg bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-60";
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-describedby={describedBy}
      className={className}
    >
      {children}
    </button>
  );
}
