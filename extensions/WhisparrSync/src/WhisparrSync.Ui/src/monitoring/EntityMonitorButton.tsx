/**
 * The Whisparr control in a detail page's own action row, and the menu it opens.
 *
 * The host spreads its slot context as top-level props, so the props of each exported component are
 * exactly what that context carries. Only the Cove id is declared and only the Cove id is read: the
 * identifier the instance is given is re-resolved on the server from the library's own identity row,
 * and sending one from the browser is what the product forbids outright.
 *
 * The host's `Studio` and `Performer` types cannot be generated into this bundle's wire types, which
 * are emitted from this extension's own registrations, so these two prop shapes are hand-declared at
 * their narrowest and their field names are pinned in a test against the host source.
 */
import { useRef, useState } from "react";
import { createPortal } from "react-dom";

import { AsyncRegion } from "../common/ui/AsyncRegion";
import { deriveAsyncRegionState } from "../common/ui/asyncRegionLogic";
import {
  ACTION_ABSENT_IN_THIS_VERSION,
  ACTION_DID_NOT_REACH_WHISPARR,
  MONITORED_IN_WHISPARR,
  MONITORING_COULD_NOT_BE_READ,
  MONITOR_IN_WHISPARR,
} from "../common/ui/copy";
import type { WhisparrEntityKind } from "../wire/api";
import { EntityMonitorMenu } from "./EntityMonitorMenu";
import {
  describeReflectOwnedSkip,
  monitorMenu,
  routeFor,
  type MonitorMenuItem,
} from "./monitorMenuLogic";
import { useAnchoredTo } from "./useAnchoredTo";
import { WhisparrMark } from "./WhisparrMark";
import { useMonitoring } from "./useMonitoring";

/**
 * Cove's own action-row button styling, copied verbatim from the host.
 *
 * The host exports it so extensions match its rounding, size and border, and it cannot be imported
 * across repositories. Every class in it is one the host's own source writes, which is what makes it
 * a class the host actually emits.
 *
 * `border-border` is held out of it because the host's stylesheet declares that class twice: the
 * utility `border-color` rule, and a later rule setting the `border` shorthand. At equal specificity
 * the later one wins and its shorthand resets the colour, so `border-accent` alongside it computes
 * grey rather than the accent. The monitored state therefore carries `border-accent` in its place.
 */
const HERO_ACTION_BUTTON_CLASS =
  "inline-flex h-10 w-10 items-center justify-center rounded-lg border bg-card transition-colors hover:border-accent hover:text-foreground disabled:cursor-not-allowed";

/** What that route is sent. A kind expressing no scope sends none rather than a default. */
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
  const triggerRef = useRef<HTMLButtonElement>(null);
  const noticeAt = useAnchoredTo(triggerRef);

  const view = state.view;
  const monitored = view?.monitored === true;
  const name = monitored ? MONITORED_IN_WHISPARR : MONITOR_IN_WHISPARR;
  const region = deriveAsyncRegionState(state.read);

  // Anything on its way, whether the action itself or the read that follows it, so no second gesture
  // starts before the first has settled.
  const inFlight = state.acting || (state.read.reading && view !== null);
  const menu = view === null ? null : monitorMenu(view, inFlight);

  // A reason disables and an absent reason enables, so the control cannot be dimmed with nothing to
  // hear. A read that failed reports that it failed: falling back to the unmonitored look would be a
  // confident report of a fact nobody established.
  const unavailable =
    region.status === "failed"
      ? MONITORING_COULD_NOT_BE_READ
      : region.status === "reading"
        ? null
        : (menu?.available ?? true)
          ? null
          : (menu?.reason ?? MONITORING_COULD_NOT_BE_READ);

  // With no visible label the accessible name is the only name the control has, so it leads and the
  // reason follows it, and the hover text is that same string.
  const spoken = unavailable === null ? name : `${name}, ${unavailable}`;

  // A skip is a settled answer and a failure is no answer at all, so each has its own sentence. The
  // failure reads first: an action that never arrived cannot also have been skipped.
  const outcome =
    state.actionError !== null
      ? ACTION_DID_NOT_REACH_WHISPARR
      : state.actionSkip === null
        ? null
        : describeReflectOwnedSkip(state.actionSkip);

  const items = (menu?.items ?? []).map((item) =>
    routeFor(item, monitored) === null
      ? { ...item, enabled: false, reason: item.reason ?? ACTION_ABSENT_IN_THIS_VERSION }
      : item,
  );

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
          // Until the read answers, the bordered shell alone. The mark is full colour and names the
          // connected generation by that colour, so painting one before the read would be a chance
          // of showing the wrong product. A failed read draws no mark for the same reason, and says
          // so through the control's own name rather than through a mark it would have to guess.
          reading={null}
          empty={null}
          failed={null}
          content={
            <>
              {monitored ? (
                // The state lives on the border and the tick. The mark is a filled two-tone disc, so
                // it can neither invert on a fill nor dim; a tint behind it lifts the border's one
                // pixel without competing with the mark's own colour.
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

      {open && menu !== null ? (
        <EntityMonitorMenu
          menu={{ ...menu, items }}
          label={name}
          triggerRef={triggerRef}
          // The menu stays open while the action runs. Every item disables until the state has been
          // read back, so what the reader sees next is what the instance answered rather than the
          // menu they pressed disappearing before it changed.
          onSelect={(item) => {
            const route = routeFor(item, monitored);
            if (route === null) return;
            act(route, bodyFor(item));
          }}
          onClose={() => {
            setOpen(false);
          }}
        />
      ) : null}

      {outcome === null
        ? null
        : // Beneath the control rather than in place of it: the control still reports what the
          // entity is, and this reports what the last gesture did. Portaled for the reason the menu
          // is - the host's hero clips its children, and `z-50` does not escape that.
          createPortal(
            <p
              role="status"
              style={noticeAt}
              className="fixed z-50 w-72 rounded-lg border border-border bg-surface px-3 py-2 text-xs text-secondary shadow-xl"
            >
              {outcome}
            </p>,
            document.body,
          )}
    </div>
  );
}
