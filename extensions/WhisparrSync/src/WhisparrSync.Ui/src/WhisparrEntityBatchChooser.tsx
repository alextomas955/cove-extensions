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
import { useLayoutEffect, useRef } from "react";
import { createRoot } from "react-dom/client";
import { CircleSlash, FileCheck2, PlusCircle, Radar, Search } from "lucide-react";
import { WhisparrLogo } from "./WhisparrLogo";
import type { EntityBatchKind, EntityBatchMenuItem, EntityBatchOp } from "./entitiesBatchLogic";

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

  useLayoutEffect(() => {
    const nodes = () =>
      Array.from(menuRef.current?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? []);
    nodes()[0]?.focus();

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
        const list = nodes();
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

      {items.map((item, i) => {
        const Icon = OP_ICON[item.op];
        return (
          <button
            key={`${item.op}:${item.scope ?? ""}:${String(i)}`}
            type="button"
            role="menuitem"
            onClick={() => {
              onPick(item);
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
 * Mount the chooser imperatively and resolve with the picked {@link EntityBatchMenuItem}, or `null` on cancel
 * (Escape / outside-click). Called by the bulk-action handler, which has no React tree of its own.
 */
export function presentEntityBatchChooser(
  count: number,
  kind: EntityBatchKind,
  items: EntityBatchMenuItem[],
): Promise<EntityBatchMenuItem | null> {
  return new Promise<EntityBatchMenuItem | null>((resolve) => {
    const container = document.createElement("div");
    document.body.appendChild(container);
    const root = createRoot(container);

    let settled = false;
    function finish(result: EntityBatchMenuItem | null) {
      if (settled) return;
      settled = true;
      root.unmount();
      container.remove();
      resolve(result);
    }

    root.render(
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
      />,
    );
  });
}
