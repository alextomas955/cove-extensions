/**
 * WhisparrEntityBatchChooser — the studios/performers-list bulk chooser, the entity analogue of
 * {@link ./WhisparrBatchChooser}. The host dispatches the single "Whisparr" bulk action to
 * {@link ./whisparrEntitiesBatchSelected} with the selection; that handler resolves the connected version,
 * computes the version+kind-gated ops ({@link ./entitiesBatchLogic}), and mounts THIS chooser imperatively via
 * {@link presentEntityBatchChooser} to await the pick.
 *
 * Same interaction contract as WhisparrBatchChooser/WhisparrMenu (branded popover, Escape + outside-click close,
 * arrow-key nav, fixed-centered near the top since there is no trigger to anchor to). The op list is passed IN
 * (already gated), so the chooser is presentational; a Monitor item carries its scope so the pick returns both.
 * All labels are React text nodes (auto-escaped); host Tailwind token classes only (check-classes).
 */
// Co-locates the component with its imperative mounter (presentEntityBatchChooser), exactly as
// WhisparrBatchChooser does — the fast-refresh components-only rule is a dev-HMR concern that does not apply to
// this production library bundle.
/* eslint-disable react-refresh/only-export-components */
import { useRef } from "react";
import { CircleSlash, FileCheck2, PlusCircle, Radar, Search } from "lucide-react";
import { StatusText, presentOverlay, useOverlayKeys } from "@cove-extensions/ui-shared";
import { WhisparrLogo } from "../common/ui/WhisparrLogo";
import type { EntityBatchKind, EntityBatchMenuItem, EntityBatchOp } from "./entitiesBatchLogic";
import { configGuardMessage, configShortReason } from "../common/lib/configGuardLogic";
import { useConfigHealth } from "../common/lib/configHealthStore";
import { guardedControl } from "../common/lib/refusalAffordanceLogic";

/** The lucide glyph per op — glyph + label, never color-only. Both Monitor items share the Radar glyph. */
const OP_ICON: Record<EntityBatchOp, typeof Radar> = {
  monitor: Radar,
  unmonitor: CircleSlash,
  addMissing: PlusCircle,
  search: Search,
  reflectOwned: FileCheck2,
};

const MENU_WIDTH = 300;

function EntityBatchChooser({
  count,
  kind,
  items,
  onPick,
  onCancel,
}: {
  count: number;
  kind: EntityBatchKind;
  items: EntityBatchMenuItem[];
  onPick: (item: EntityBatchMenuItem) => void;
  onCancel: () => void;
}) {
  const menuRef = useRef<HTMLDivElement>(null);
  const config = useConfigHealth();

  // Same interaction contract as WhisparrBatchChooser / WhisparrMenu.
  useOverlayKeys(menuRef, { nav: "menu", onClose: onCancel });

  // Gated on !loading so an in-flight read never dims an item; a failed read reports nothing unmet. The chooser
  // itself is never withheld — the user opened it on purpose, and every item names its own requirement.
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
  const noun = kind === "performer" ? "performers" : "studios";

  return (
    <div
      ref={menuRef}
      role="menu"
      aria-label={`Whisparr · ${count} ${noun}`}
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
      <div className="flex items-center gap-2 px-3 py-2">
        <WhisparrLogo className="h-4 w-4 text-secondary" />
        <span className="text-xs font-semibold uppercase tracking-wide text-secondary">
          Whisparr · {count} {noun}
        </span>
      </div>

      {configSentence !== null && (
        <div role="status" className="border-t border-border px-3 py-2">
          <StatusText kind="warning">{configSentence}</StatusText>
        </div>
      )}

      {items.map((item, i) => {
        const Icon = OP_ICON[item.op];
        const affordance = guardedControl({
          name: item.label,
          configurationReason: configRequirement,
        });
        return (
          <button
            key={`${item.op}:${item.scope ?? ""}:${String(i)}`}
            type="button"
            role="menuitem"
            disabled={affordance.disabled}
            title={affordance.title}
            aria-label={affordance.ariaLabel}
            onClick={() => {
              onPick(item);
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
 * Mount the chooser imperatively and resolve with the picked {@link EntityBatchMenuItem}, or `null` on cancel
 * (Escape / outside-click). Called by the bulk-action handler, which has no React tree of its own.
 */
export function presentEntityBatchChooser(
  count: number,
  kind: EntityBatchKind,
  items: EntityBatchMenuItem[],
): Promise<EntityBatchMenuItem | null> {
  return presentOverlay<EntityBatchMenuItem>((finish) => (
    <EntityBatchChooser
      count={count}
      kind={kind}
      items={items}
      onPick={(item) => {
        finish(item);
      }}
      onCancel={() => {
        finish(null);
      }}
    />
  ));
}
