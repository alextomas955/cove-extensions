/**
 * WhisparrActivityTabs — the segmented tab bar at the top of the activity sub-page, one tab per
 * `ACTIVITY_SECTIONS` entry: label, order and whether a tab carries a count Badge all come from the
 * descriptor, so a grouping is added by appending a row to that table rather than a branch here. Reuses the
 * version-selector active-pill idiom (accent-tinted active, hover-accent inactive). It is an ARIA tablist:
 * each tab is `role="tab"` with `aria-selected`, arrow/Home/End keys rove focus + activate, and only the
 * active tab is in the tab order (roving `tabIndex`). Presentational — the parent owns the active-tab state
 * (and its URL-hash persistence) and the count values.
 */
import type { KeyboardEvent } from "react";
import { useRef } from "react";
import { Badge } from "@cove-extensions/ui-shared";
import { ACTIVITY_SECTIONS, ACTIVITY_TAB_ORDER, type ActivityTab } from "./activityLogic";

export function WhisparrActivityTabs({
  active,
  onSelect,
  counts,
}: {
  active: ActivityTab;
  onSelect: (tab: ActivityTab) => void;
  /** Each section's live total, or null while loading/errored/unopened — a null count hides the badge. */
  counts: Partial<Record<ActivityTab, number | null>>;
}) {
  const refs = useRef<Partial<Record<ActivityTab, HTMLButtonElement | null>>>({});

  const onKeyDown = (e: KeyboardEvent<HTMLButtonElement>) => {
    const idx = ACTIVITY_TAB_ORDER.indexOf(active);
    const last = ACTIVITY_TAB_ORDER.length - 1;
    let next: ActivityTab | null = null;
    if (e.key === "ArrowRight") next = ACTIVITY_TAB_ORDER[idx === last ? 0 : idx + 1];
    else if (e.key === "ArrowLeft") next = ACTIVITY_TAB_ORDER[idx === 0 ? last : idx - 1];
    else if (e.key === "Home") next = ACTIVITY_TAB_ORDER[0];
    else if (e.key === "End") next = ACTIVITY_TAB_ORDER[last];
    if (next !== null) {
      e.preventDefault();
      onSelect(next);
      refs.current[next]?.focus();
    }
  };

  return (
    <div role="tablist" aria-label="Activity sections" className="flex items-center gap-2">
      {ACTIVITY_SECTIONS.map((section) => {
        const tab = section.key;
        const isActive = tab === active;
        const count = section.showsCount ? (counts[tab] ?? null) : null;
        return (
          <button
            key={tab}
            ref={(el) => {
              refs.current[tab] = el;
            }}
            type="button"
            role="tab"
            id={`activity-tab-${tab}`}
            aria-selected={isActive}
            aria-controls="activity-panel"
            tabIndex={isActive ? 0 : -1}
            onClick={() => {
              onSelect(tab);
            }}
            onKeyDown={onKeyDown}
            className={`inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-sm font-medium transition-colors ${
              isActive
                ? "border-accent bg-accent/10 text-accent"
                : "border-border bg-card text-secondary hover:border-accent/50 hover:text-foreground"
            }`}
          >
            {section.label}
            {count !== null ? (
              <Badge>
                <span className="tabular-nums">{count}</span>
              </Badge>
            ) : null}
          </button>
        );
      })}
    </div>
  );
}
