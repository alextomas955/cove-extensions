/**
 * WhisparrBatchChooser — the videos-list bulk chooser. The host dispatches the single "Whisparr"
 * bulk action to the {@link ./whisparrBatchSelected} handler with the selection; a flat bulk-action button can't
 * itself present the design's six ordered sub-items (Add · Monitor · Unmonitor · Search now · Search for
 * upgrades · Exclude), so the handler mounts THIS chooser imperatively via {@link presentBatchChooser} and
 * awaits the user's pick.
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
import { useRef } from "react";
import { Ban, CircleSlash, Plus, Radar, Search, TrendingUp } from "lucide-react";
import { StatusText, presentOverlay, useOverlayKeys } from "@cove-extensions/ui-shared";
import { WhisparrLogo } from "../common/ui/WhisparrLogo";
import { BATCH_MENU_ITEMS, type BatchOp } from "../common/lib/sceneActionsLogic";
import { configGuardMessage, configShortReason } from "../common/lib/configGuardLogic";
import { useConfigHealth } from "../common/lib/configHealthStore";
import { guardedControl } from "../common/lib/refusalAffordanceLogic";

/** The lucide glyph for each batch op — glyph + label, never color-only. Monitor/Unmonitor mirror the entity batch chooser's glyphs. */
const OP_ICON: Record<BatchOp, typeof Plus> = {
  add: Plus,
  monitor: Radar,
  unmonitor: CircleSlash,
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
  const config = useConfigHealth();

  // The WhisparrMenu interaction contract (focus-first, arrow-nav, capture-phase Escape + outside-click).
  useOverlayKeys(menuRef, { nav: "menu", onClose: onCancel });

  // Gated on !loading so an in-flight read never dims an item; a failed read reports nothing unmet. The chooser as
  // a whole stays open: the user opened it on purpose, and every item names its own requirement.
  const configIncomplete = !config.loading && config.missingRequiredOptions.length > 0;
  // The chooser states the full sentence once; each item it dims carries the short requirement.
  const configSentence = configIncomplete
    ? configGuardMessage(config.missingRequiredOptions)
    : null;
  const configRequirement = configIncomplete
    ? configShortReason(config.missingRequiredOptions)
    : null;

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

      {configSentence !== null && (
        <div role="status" className="border-t border-border px-3 py-2">
          <StatusText kind="warning">{configSentence}</StatusText>
        </div>
      )}

      {BATCH_MENU_ITEMS.map((item) => {
        const Icon = OP_ICON[item.op];
        const affordance = guardedControl({
          name: item.label,
          configurationReason: configRequirement,
        });
        return (
          <button
            key={item.op}
            type="button"
            role="menuitem"
            disabled={affordance.disabled}
            title={affordance.title}
            aria-label={affordance.ariaLabel}
            onClick={() => {
              onPick(item.op);
            }}
            className={`${itemBase} disabled:cursor-not-allowed disabled:opacity-60`}
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
  return presentOverlay<BatchOp>((finish) => (
    <BatchChooser
      count={count}
      onPick={(op) => {
        finish(op);
      }}
      onCancel={() => {
        finish(null);
      }}
    />
  ));
}
