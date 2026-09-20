/**
 * The Whisparr control in a detail page's own action row, and the menu it opens.
 *
 * The host spreads its slot context as top-level props, so each exported component's props are
 * exactly what that context carries. Only the Cove id is declared and read: the identifier the
 * instance is given is re-resolved on the server.
 *
 * The host's `Studio` and `Performer` types cannot be generated into this bundle's wire types, so
 * these two prop shapes are hand-declared and pinned in a test against the host source.
 */
import { useRef, useState } from "react";
import { createPortal } from "react-dom";

import { AsyncRegion } from "../common/ui/AsyncRegion";
import { deriveAsyncRegionState } from "../common/ui/asyncRegionLogic";
import {
  ACTION_ABSENT_IN_THIS_VERSION,
  allScenesConfirmation,
  MONITORING_COULD_NOT_BE_READ,
  WHISPARR_MONITORED,
  WHISPARR_NOT_MONITORED,
} from "../common/ui/copy";
import type { WhisparrEntityKind } from "../wire/api";
import { EntityMonitorMenu, NOTICE_SURFACE_CLASS } from "./EntityMonitorMenu";
import { ConfirmDialog } from "./hostComponents";
import {
  allScenesIsAOneWayDoor,
  controlNotice,
  marksTheBackCatalogue,
  monitorMenu,
  routeFor,
  type MonitorActionRoute,
  type MonitorMenuItem,
} from "./monitorMenuLogic";
import { useAnchoredTo } from "./useAnchoredTo";
import { WhisparrMark } from "./WhisparrMark";
import { useMonitoring } from "./useMonitoring";

/**
 * Cove's own action-row button styling, copied verbatim from the host because it cannot be imported
 * across repositories. Every class in it is one the host's source writes, so the host emits it.
 *
 * `border-border` is held out. The host's stylesheet declares that class twice, and the later rule
 * sets the `border` shorthand, which resets the colour. `border-accent` alongside it would compute
 * grey, so the monitored state carries `border-accent` in its place.
 */
const HERO_ACTION_BUTTON_CLASS =
  "inline-flex h-10 w-10 items-center justify-center rounded-lg border bg-card transition-colors hover:border-accent hover:text-foreground disabled:cursor-not-allowed";

// An item expressing no scope sends none rather than a default.
function bodyFor(item: MonitorMenuItem): unknown {
  return { scope: item.item === "scope" ? item.scope : null };
}

export function WhisparrStudioActions({ studio }: { studio: { id: number } }) {
  return <EntityMonitorControl kind="studio" coveId={studio.id} />;
}

export function WhisparrPerformerActions({ performer }: { performer: { id: number } }) {
  return <EntityMonitorControl kind="performer" coveId={performer.id} />;
}

function EntityMonitorControl({ kind, coveId }: { kind: WhisparrEntityKind; coveId: number }) {
  const { state, act } = useMonitoring(kind, coveId);
  const [open, setOpen] = useState(false);
  const [confirming, setConfirming] = useState<{
    item: MonitorMenuItem;
    route: MonitorActionRoute;
  } | null>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const placement = useAnchoredTo(triggerRef);

  const view = state.view;
  const monitored = view?.monitored === true;
  const name = monitored ? WHISPARR_MONITORED : WHISPARR_NOT_MONITORED;
  const region = deriveAsyncRegionState(state.read);

  // The action or the read that follows it, so no second gesture starts before the first settles.
  const inFlight = state.acting || (state.read.reading && view !== null);
  const menu = view === null ? null : monitorMenu(view, inFlight);

  // A reason disables and an absent reason enables, so the control cannot be dimmed with nothing
  // to hear. A failed read says so rather than falling back to the unmonitored look, which would
  // report a fact nobody established.
  const unavailable =
    region.status === "failed"
      ? MONITORING_COULD_NOT_BE_READ
      : region.status === "reading"
        ? null
        : (menu?.available ?? true)
          ? null
          : (menu?.reason ?? MONITORING_COULD_NOT_BE_READ);

  // With no visible label the accessible name is the only name the control has, so it leads and
  // the reason follows it. The hover text is that same string.
  const spoken = unavailable === null ? name : `${name}, ${unavailable}`;

  // A press refusal outranks the view's own, and a failed read contributes none: the control
  // already says the read failed. The rule for which refusal speaks where lives in `controlNotice`
  // alone, so nothing is filtered here.
  const outcome = controlNotice({
    failed: state.actionFailed,
    refusal: state.actionRefusal ?? (region.status === "failed" ? null : (view?.refusal ?? null)),
    skip: state.actionSkip,
  });

  const items = (menu?.items ?? []).map((item) =>
    routeFor(item, monitored) === null
      ? { ...item, reason: item.reason ?? ACTION_ABSENT_IN_THIS_VERSION }
      : item,
  );

  // One representation of "the menu is on screen", so the notice's two homes cannot both render.
  const openMenu = open ? menu : null;

  return (
    <div>
      <button
        ref={triggerRef}
        type="button"
        className={
          monitored
            ? `relative ${HERO_ACTION_BUTTON_CLASS} border-accent`
            : `relative ${HERO_ACTION_BUTTON_CLASS} border-border`
        }
        disabled={unavailable !== null || region.status === "reading"}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={spoken}
        title={spoken}
        onClick={() => {
          setOpen(!open);
        }}
      >
        <AsyncRegion
          state={region}
          // The bordered shell alone until the read answers. The mark names the connected
          // generation by its colour, so painting one before the read could show the wrong product.
          // A failed read draws no mark for the same reason and says so through the control's name.
          reading={null}
          empty={null}
          failed={null}
          content={
            <>
              {monitored ? (
                // The mark is a filled two-tone disc, so it can neither invert on a fill nor dim. A
                // tint behind it lifts the border without competing with the mark's own colour.
                <span className="absolute inset-0 rounded-lg bg-accent/10" />
              ) : null}
              <WhisparrMark generation={view?.generation} className="relative h-5 w-5" />
              {monitored ? (
                <svg
                  viewBox="0 0 24 24"
                  className="absolute bottom-0 right-0 h-3 w-3 rounded-full bg-card text-accent"
                  aria-hidden="true"
                  focusable="false"
                  role="presentation"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="3"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                >
                  <path d="M20 6 9 17l-5-5" />
                </svg>
              ) : null}
            </>
          }
        />
      </button>

      {openMenu === null ? null : (
        <EntityMonitorMenu
          menu={{ ...openMenu, items }}
          label={name}
          triggerRef={triggerRef}
          // Inside the menu's own container, so it sits below the rows it reports on rather than
          // over them.
          notice={outcome}
          // The menu stays open while the action runs and every item disables until the state has
          // been read back, so what the reader sees next is what the instance answered.
          onSelect={(item) => {
            const route = routeFor(item, monitored);
            if (route === null) return;
            if (marksTheBackCatalogue(item)) {
              setConfirming({ item, route });
              return;
            }
            act(route, bodyFor(item));
          }}
          onClose={() => {
            setOpen(false);
          }}
        />
      )}

      {confirming === null
        ? null
        : // Portaled for the reason the menu is: the host's hero clips its children, and the
          // dialog's own `fixed inset-0` would be cut to that rectangle rather than cover the page.
          createPortal(
            <ConfirmDialog
              open
              // Red where the action cannot be undone and accent where it can, rather than the
              // host's default of always red.
              destructive={allScenesIsAOneWayDoor(view?.generation ?? null)}
              title={confirming.item.label}
              confirmLabel={confirming.item.label}
              message={allScenesConfirmation(1, allScenesIsAOneWayDoor(view?.generation ?? null))}
              onConfirm={() => {
                act(confirming.route, bodyFor(confirming.item));
                setConfirming(null);
              }}
              onCancel={() => {
                setConfirming(null);
              }}
            />,
            document.body,
          )}

      {outcome === null || openMenu !== null
        ? null
        : // Beneath the control rather than in place of it: the control reports what the entity is
          // and this reports what the last gesture did. Portaled for the reason the menu is, since
          // `z-50` does not escape the clipping hero. With the menu open its container holds this
          // instead, so the two do not stack on one another.
          createPortal(
            <p
              role="status"
              style={placement.at}
              className={`fixed z-50 w-72 ${NOTICE_SURFACE_CLASS}`}
            >
              {outcome}
            </p>,
            document.body,
          )}
    </div>
  );
}
