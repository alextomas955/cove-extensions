/**
 * WhisparrBatchChooser — the videos-list bulk chooser. The host dispatches the single "Whisparr"
 * bulk action to the {@link ./whisparrBatchSelected} handler with the selection; a flat bulk-action button can't
 * itself present the design's four ordered sub-items (Add · Search now · Search for upgrades · Exclude), so the
 * handler mounts THIS chooser imperatively via {@link presentBatchChooser} and awaits the user's pick.
 *
 * It reuses the {@link ./WhisparrMenu} interaction contract — a branded popover that closes on Escape +
 * outside-click and is arrow-key navigable — but has no trigger to anchor to (it's opened from a host action,
 * not a button we own), so it fixed-centers near the top of the viewport via an inline transform (the extension
 * ships no CSS; a `left-1/2`/`-translate-x-1/2` utility the host never emits would not position it). All labels
 * render as React text nodes (auto-escaped); styling uses host Tailwind token classes only (check-classes).
 */
// This file intentionally co-locates the chooser component with its imperative mounter
// (presentBatchChooser renders the component into a body-attached root). Fast-refresh's
// components-only export rule is a dev-HMR concern that does not apply to this production
// library bundle, and splitting the mounter from the component it renders would be artificial.
/* eslint-disable react-refresh/only-export-components */
import { useLayoutEffect, useRef } from "react";
import { createRoot } from "react-dom/client";
import { Ban, Plus, Search, TrendingUp } from "lucide-react";
import { WhisparrLogo } from "./WhisparrLogo";
import { BATCH_MENU_ITEMS, type BatchOp } from "./sceneActionsLogic";

/** The lucide glyph for each batch op — glyph + label, never color-only. */
const OP_ICON: Record<BatchOp, typeof Plus> = {
  add: Plus,
  search: Search,
  searchUpgrades: TrendingUp,
  exclude: Ban,
};

/** The fixed menu width (px); centered horizontally so it never overflows off-screen. */
const MENU_WIDTH = 288;

function BatchChooser({
  count,
  onPick,
  onCancel,
}: {
  count: number;
  onPick: (op: BatchOp) => void;
  onCancel: () => void;
}) {
  const menuRef = useRef<HTMLDivElement>(null);

  // Focus the first item on open, and wire outside-click + Escape + arrow-key navigation (the WhisparrMenu
  // contract). Listeners are on the capture phase so the chooser wins over host handlers; cleanup removes them.
  useLayoutEffect(() => {
    const items = () =>
      Array.from(menuRef.current?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? []);
    items()[0]?.focus();

    function onPointerDown(e: PointerEvent) {
      if (menuRef.current?.contains(e.target as Node)) return;
      onCancel();
    }
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === "Escape") {
        e.stopPropagation();
        onCancel();
        return;
      }
      if (e.key === "ArrowDown" || e.key === "ArrowUp") {
        e.preventDefault();
        const list = items();
        if (list.length === 0) return;
        const current = list.indexOf(document.activeElement as HTMLElement);
        const next =
          e.key === "ArrowDown"
            ? (current + 1) % list.length
            : (current - 1 + list.length) % list.length;
        list[next]?.focus();
      }
    }

    document.addEventListener("pointerdown", onPointerDown, true);
    document.addEventListener("keydown", onKeyDown, true);
    return () => {
      document.removeEventListener("pointerdown", onPointerDown, true);
      document.removeEventListener("keydown", onKeyDown, true);
    };
  }, [onCancel]);

  const itemBase =
    "flex w-full items-center gap-2 rounded-md px-3 py-2 text-left text-sm text-foreground transition-colors hover:bg-card focus:bg-card focus:outline-none";

  return (
    <div
      ref={menuRef}
      role="menu"
      aria-label={`Whisparr · ${count} items`}
      style={{
        position: "fixed",
        top: "20vh",
        left: "50%",
        transform: "translateX(-50%)",
        width: MENU_WIDTH,
        zIndex: 60,
      }}
      className="flex flex-col gap-1 rounded-lg border border-border bg-background p-1 shadow-lg"
    >
      {/* Brand header + the design title "Whisparr · {N} items". */}
      <div className="flex items-center gap-2 px-3 py-2">
        <WhisparrLogo className="h-4 w-4 text-secondary" />
        <span className="text-xs font-semibold uppercase tracking-wide text-secondary">
          Whisparr · {count} items
        </span>
      </div>

      {BATCH_MENU_ITEMS.map((item) => {
        const Icon = OP_ICON[item.op];
        return (
          <button
            key={item.op}
            type="button"
            role="menuitem"
            onClick={() => {
              onPick(item.op);
            }}
            className={itemBase}
          >
            <Icon className="h-4 w-4 text-secondary" />
            <span className="flex-1">{item.label}</span>
          </button>
        );
      })}
    </div>
  );
}

/**
 * Mount the chooser imperatively and resolve with the picked {@link BatchOp}, or `null` on cancel
 * (Escape / outside-click). Called by the bulk-action handler, which has no React tree of its own — so this
 * creates a body-attached root, renders {@link BatchChooser}, and tears the root down once the user resolves it.
 */
export function presentBatchChooser(count: number): Promise<BatchOp | null> {
  return new Promise<BatchOp | null>((resolve) => {
    const container = document.createElement("div");
    document.body.appendChild(container);
    const root = createRoot(container);

    let settled = false;
    function finish(result: BatchOp | null) {
      if (settled) return;
      settled = true;
      root.unmount();
      container.remove();
      resolve(result);
    }

    root.render(
      <BatchChooser
        count={count}
        onPick={(op) => {
          finish(op);
        }}
        onCancel={() => {
          finish(null);
        }}
      />,
    );
  });
}
