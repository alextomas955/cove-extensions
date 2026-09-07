/**
 * A searchable single-select (editable combobox) for facet filters where the option set runs to
 * hundreds of values (performers, tags, sub-studios) — a native `<select>` is unusable at that size.
 * Mimics Cove core's autocomplete pattern (`AutocompleteDropdown` + `useAutocomplete`): a trigger that
 * reads like a `<select>`, and an anchor-positioned dropdown holding a text filter over the options.
 *
 * The dropdown renders through a portal so it escapes the toolbar segment's clipping/stacking (the
 * same reason Cove's AutocompleteDropdown portals). Focus stays in the filter input while ArrowUp/Down
 * move a highlight (aria-activedescendant), Enter commits, Escape/outside-click close — the shared
 * {@link useOverlayKeys} owns focus-on-open + Escape + outside-click. Every class is a host-emitted
 * utility (`z-[200]` matches Cove's AutocompleteDropdown verbatim and is whitelisted in check-classes);
 * option labels render as React text nodes only.
 */
import { useLayoutEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Check, ChevronDown, Search } from "lucide-react";
import { useOverlayKeys } from "@cove-extensions/ui-shared";
import {
  filterOptions,
  nextActiveIndex,
  type SearchableOption,
} from "../lib/searchableSelectLogic";

const TRIGGER_CLASS =
  "inline-flex min-h-10 items-center justify-between gap-1.5 rounded-md border border-border/60 bg-input px-2.5 py-2 text-sm text-foreground shadow-inner focus:border-accent focus:outline-none sm:min-h-[30px] sm:px-2 sm:py-1 sm:text-xs";

// A fixed id, not a per-instance one: only the open dropdown ever renders the listbox, at most one
// popover is mounted at a time, and a collapsed trigger's reference to the absent element resolves to
// nothing, which assistive technology ignores.
const LISTBOX_ID = "ws-searchable-listbox";

export function SearchableSelect({
  value,
  onChange,
  options,
  ariaLabel,
  searchPlaceholder,
}: {
  value: string;
  onChange: (value: string) => void;
  options: readonly SearchableOption[];
  ariaLabel: string;
  searchPlaceholder?: string;
}) {
  const [open, setOpen] = useState(false);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const selectedLabel = options.find((option) => option.value === value)?.label ?? "";

  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        role="combobox"
        aria-label={ariaLabel}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={LISTBOX_ID}
        onClick={() => {
          setOpen((prev) => !prev);
        }}
        className={TRIGGER_CLASS}
      >
        <span className="truncate">{selectedLabel}</span>
        <ChevronDown className="h-3.5 w-3.5 shrink-0 text-muted" aria-hidden />
      </button>
      {open && (
        <SearchablePopover
          anchorRef={triggerRef}
          options={options}
          value={value}
          ariaLabel={ariaLabel}
          searchPlaceholder={searchPlaceholder}
          onSelect={(next) => {
            onChange(next);
            setOpen(false);
            triggerRef.current?.focus();
          }}
          onClose={() => {
            setOpen(false);
          }}
        />
      )}
    </>
  );
}

interface Placement {
  left: number;
  top: number;
  width: number;
  maxHeight: number;
  above: boolean;
}

function SearchablePopover({
  anchorRef,
  options,
  value,
  ariaLabel,
  searchPlaceholder,
  onSelect,
  onClose,
}: {
  anchorRef: React.RefObject<HTMLButtonElement | null>;
  options: readonly SearchableOption[];
  value: string;
  ariaLabel: string;
  searchPlaceholder?: string;
  onSelect: (value: string) => void;
  onClose: () => void;
}) {
  const panelRef = useRef<HTMLDivElement>(null);
  const listboxRef = useRef<HTMLDivElement>(null);
  const [query, setQuery] = useState("");
  const [activeIndex, setActiveIndex] = useState(0);
  const [placement, setPlacement] = useState<Placement | null>(null);

  // Escape (capture-phase, so it beats host key handlers) + outside-click close, and focus-first onto
  // the filter input. The trigger is excluded from "outside" so its click toggles rather than double-fires.
  useOverlayKeys(panelRef, {
    nav: "menu",
    itemSelector: "input",
    onClose,
    excludeRefs: [anchorRef],
  });

  const filtered = useMemo(() => filterOptions(options, query), [options, query]);

  // Anchor the dropdown to the trigger, flipping above when there isn't room below — the same
  // placement math as Cove's AutocompleteDropdown, kept on scroll/resize while open.
  useLayoutEffect(() => {
    const place = () => {
      const anchor = anchorRef.current;
      if (!anchor) return;
      const rect = anchor.getBoundingClientRect();
      const gap = 4;
      const maxHeight = 260;
      const spaceBelow = window.innerHeight - rect.bottom - gap;
      const spaceAbove = rect.top - gap;
      const above = spaceBelow < maxHeight && spaceAbove > spaceBelow;
      setPlacement({
        left: rect.left + window.scrollX,
        top: (above ? rect.top - gap : rect.bottom + gap) + window.scrollY,
        // A very short trigger (a short selected label) would give an unusably narrow list; floor the
        // dropdown at a readable width so long option labels aren't perpetually truncated.
        width: Math.max(rect.width, 220),
        maxHeight: Math.min(maxHeight, Math.max(0, above ? spaceAbove : spaceBelow)),
        above,
      });
    };
    place();
    window.addEventListener("resize", place);
    window.addEventListener("scroll", place, true);
    return () => {
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
    };
  }, [anchorRef]);

  // Keep the highlight in range as the filter narrows, and scroll it into view.
  const clampedActive = filtered.length === 0 ? -1 : Math.min(activeIndex, filtered.length - 1);
  useLayoutEffect(() => {
    if (clampedActive < 0) return;
    listboxRef.current
      ?.querySelector<HTMLElement>(`[data-index="${clampedActive.toString()}"]`)
      ?.scrollIntoView({ block: "nearest" });
  }, [clampedActive]);

  if (placement === null) return null;

  const optionId = (index: number) => `${LISTBOX_ID}-option-${index.toString()}`;

  return createPortal(
    <div
      ref={panelRef}
      className="absolute z-[200] overflow-hidden rounded-lg border border-border bg-surface shadow-lg"
      style={{
        left: placement.left,
        top: placement.top,
        width: placement.width,
        transform: placement.above ? "translateY(-100%)" : undefined,
      }}
    >
      <div className="relative border-b border-border p-1.5">
        <Search
          className="pointer-events-none absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted"
          aria-hidden
        />
        <input
          type="text"
          value={query}
          role="combobox"
          aria-autocomplete="list"
          aria-expanded
          aria-controls={LISTBOX_ID}
          aria-activedescendant={clampedActive < 0 ? undefined : optionId(clampedActive)}
          aria-label={`Search ${ariaLabel.replace(/^Filter by /, "")}`}
          placeholder={searchPlaceholder ?? "Search…"}
          onChange={(e) => {
            setQuery(e.target.value);
            setActiveIndex(0);
          }}
          onKeyDown={(e) => {
            if (e.key === "ArrowDown") {
              e.preventDefault();
              setActiveIndex((current) => nextActiveIndex(current, filtered.length, 1));
            } else if (e.key === "ArrowUp") {
              e.preventDefault();
              setActiveIndex((current) => nextActiveIndex(current, filtered.length, -1));
            } else if (e.key === "Enter") {
              e.preventDefault();
              if (clampedActive >= 0) onSelect(filtered[clampedActive].value);
            }
          }}
          className="w-full rounded-md border border-border/60 bg-input py-1.5 pl-9 pr-2 text-xs text-foreground shadow-inner focus:border-accent focus:outline-none"
        />
      </div>
      <div
        ref={listboxRef}
        id={LISTBOX_ID}
        role="listbox"
        aria-label={ariaLabel}
        className="overflow-y-auto py-1"
        style={{ maxHeight: placement.maxHeight - 44 }}
      >
        {filtered.length === 0 ? (
          <p className="px-3 py-2 text-xs text-muted">No matches</p>
        ) : (
          filtered.map((option, index) => {
            const selected = option.value === value;
            const active = index === clampedActive;
            return (
              <button
                key={option.value || "__all__"}
                type="button"
                role="option"
                id={optionId(index)}
                data-index={index}
                aria-selected={selected}
                onMouseMove={() => {
                  setActiveIndex(index);
                }}
                onMouseDown={(e) => {
                  e.preventDefault();
                }}
                onClick={() => {
                  onSelect(option.value);
                }}
                className={`flex w-full items-center justify-between gap-2 px-3 py-1.5 text-left text-xs ${
                  active ? "bg-accent/15 text-foreground" : "text-secondary"
                }`}
              >
                <span className="truncate">{option.label}</span>
                {selected ? (
                  <Check className="h-3.5 w-3.5 shrink-0 text-accent" aria-hidden />
                ) : null}
              </button>
            );
          })
        )}
      </div>
    </div>,
    document.body,
  );
}
