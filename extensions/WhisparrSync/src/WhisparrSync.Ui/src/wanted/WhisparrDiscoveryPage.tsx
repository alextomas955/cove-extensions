/**
 * WhisparrDiscoveryPage — the component the host mounts inside the NESTED "Wanted, queue & history" settings
 * sub-page (AddSettingsTab layout Page, parentTabKey "whisparr-sync"). Read-only: it renders one tabbed,
 * virtualized section per `ACTIVITY_SECTIONS` row — the descriptor supplies the copy, `ROW_VIEWS` the row
 * half — uniform across v3 and v2. The only interactive affordance is Refresh; there are no mutations.
 *
 * The host passes `{ onNavigate }`; this UI does not navigate, so it ignores it. Host Tailwind token classes
 * only (no hex, no arbitrary values — every computed dimension rides an element-scoped inline style, since the
 * host JIT never scans this bundle); all upstream text renders as React text nodes (auto-escaped); no
 * dangerouslySetInnerHTML.
 */
import type { ReactNode } from "react";
import { useCallback, useEffect, useRef, useState } from "react";
import {
  AlertTriangle,
  Bookmark,
  CheckCircle2,
  Clock,
  Download,
  FileQuestion,
  RefreshCw,
  XCircle,
  type LucideIcon,
} from "lucide-react";
import { useVirtualizer } from "@tanstack/react-virtual";
import {
  Button,
  ProgressBar,
  SectionCard,
  Spinner,
  StatusPill,
  StatusText,
} from "@cove-extensions/ui-shared";
import { WhisparrLogo } from "../common/ui/WhisparrLogo";
import { WHISPARR_UNAVAILABLE_COPY } from "../common/lib/whisparrCopy";
import {
  HISTORY_EVENT_META,
  QUEUE_STATE_META,
  WANTED_META,
  activityStatus,
  clampProgress,
  showingLabel,
  ACTIVITY_SECTIONS,
  ACTIVITY_TAB_ORDER,
  type ActivityGlyphMeta,
  type ActivitySectionDescriptor,
  type ActivityTab,
} from "./activityLogic";
import {
  loadMoreSection,
  refreshAll,
  useActivityStates,
  useQueuePoll,
  type ActivityRowByKey,
  type ActivityStates,
  type PagedActivityState,
} from "./activityStore";
import { WhisparrActivityTabs } from "./WhisparrActivityTabs";
import type { HistoryRow, QueueRow, WantedRow } from "../contracts";

/** The lucide glyphs every section's status meta (iconKey) resolves to — the NAMES live in the pure logic module. */
const GLYPH: Record<string, LucideIcon> = {
  Download,
  CheckCircle2,
  XCircle,
  Clock,
  AlertTriangle,
  Bookmark,
  FileQuestion,
};

// A fixed row height is what lets @tanstack/react-virtual measure without a layout pass (~48px).
const ROW_HEIGHT = 48;

// The scroll region never fully collapses even in a short viewport; it grows to fill the space below its top.
const MIN_SCROLL_HEIGHT = 240;

/** A short, locale-formatted date for a row's secondary line; falls back to the raw string when unparseable. */
function formatDate(iso: string | null): string | null {
  if (iso === null) return null;
  const parsed = new Date(iso);
  return Number.isNaN(parsed.getTime()) ? iso : parsed.toLocaleDateString();
}

function GlyphPill({ meta }: { meta: ActivityGlyphMeta }) {
  const Icon = GLYPH[meta.iconKey] ?? Download;
  return (
    <StatusPill variant={meta.variant} icon={<Icon className="h-3.5 w-3.5" />}>
      {meta.label}
    </StatusPill>
  );
}

function WantedRowView({ row }: { row: WantedRow }) {
  const date = formatDate(row.addedDate);
  return (
    <div className="flex h-full items-center gap-3">
      <GlyphPill meta={WANTED_META} />
      <div className="min-w-0 flex-1">
        <p className="truncate text-sm text-foreground" title={row.scene.sceneTitle}>
          {row.scene.sceneTitle}
        </p>
        {row.scene.studio !== null ? (
          <p className="truncate text-xs text-secondary">{row.scene.studio}</p>
        ) : null}
      </div>
      {date !== null ? (
        <span className="shrink-0 text-xs tabular-nums text-secondary">{date}</span>
      ) : null}
    </div>
  );
}

function QueueRowView({ row }: { row: QueueRow }) {
  const percent = clampProgress(row.progressPercent);
  return (
    <div className="flex h-full items-center gap-3">
      <GlyphPill meta={QUEUE_STATE_META[row.state]} />
      <div className="min-w-0 flex-1">
        <p className="truncate text-sm text-foreground" title={row.scene.sceneTitle}>
          {row.scene.sceneTitle}
        </p>
        {row.scene.studio !== null ? (
          <p className="truncate text-xs text-secondary">{row.scene.studio}</p>
        ) : null}
      </div>
      <div className="w-24 shrink-0">
        <ProgressBar percent={percent} label={`${row.scene.sceneTitle} download progress`} />
      </div>
      {row.scene.quality !== null ? (
        <span className="shrink-0 text-xs text-secondary">{row.scene.quality}</span>
      ) : null}
      {row.eta !== null ? (
        <span className="w-16 shrink-0 text-right text-xs tabular-nums text-secondary">
          {row.eta}
        </span>
      ) : null}
    </div>
  );
}

function HistoryRowView({ row }: { row: HistoryRow }) {
  const date = formatDate(row.scene.date);
  const secondary = [row.scene.studio, row.scene.quality, date]
    .filter((s) => s !== null)
    .join(" · ");
  return (
    <div className="flex h-full items-center gap-3">
      <GlyphPill meta={HISTORY_EVENT_META[row.event]} />
      <div className="min-w-0 flex-1">
        <p className="truncate text-sm text-foreground" title={row.scene.sceneTitle}>
          {row.scene.sceneTitle}
        </p>
        {secondary.length > 0 ? (
          <p className="truncate text-xs tabular-nums text-secondary">{secondary}</p>
        ) : null}
      </div>
    </div>
  );
}

/** One section's row half: how a row renders, and the React key it renders under. */
interface SectionViews<T> {
  renderRow: (row: T) => ReactNode;
  rowKey: (row: T, index: number) => string;
}

/**
 * The per-section React halves the descriptor table cannot hold — a component in the offline-gated logic module
 * breaks its gate, so the keys and copy live there and the JSX lives here. Keyed on `ActivityTab`, so a
 * grouping added to `ACTIVITY_SECTIONS` without an arm here is a typecheck failure, not a blank tab.
 */
const ROW_VIEWS: { [K in ActivityTab]: SectionViews<ActivityRowByKey[K]> } = {
  wanted: {
    renderRow: (row) => <WantedRowView row={row} />,
    rowKey: (row, i) => `${row.scene.sceneTitle}-${i.toString()}`,
  },
  queue: {
    renderRow: (row) => <QueueRowView row={row} />,
    rowKey: (row, i) => `${row.scene.sceneTitle}-${i.toString()}`,
  },
  history: {
    renderRow: (row) => <HistoryRowView row={row} />,
    rowKey: (row, i) => `${row.event}-${i.toString()}`,
  },
};

/**
 * The windowed row list: only the rows in view mount. The scroll region's height is set from a `ResizeObserver`
 * on the viewport (no responsive Tailwind variant, which this bundle does not reliably emit); every computed
 * dimension — row height, virtualizer offsets, total size — rides an element-scoped inline `style`.
 */
function VirtualRows<T>({
  rows,
  renderRow,
  rowKey,
}: {
  rows: readonly T[];
  renderRow: (row: T) => ReactNode;
  rowKey: (row: T, index: number) => string;
}) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const [scrollHeight, setScrollHeight] = useState(480);

  useEffect(() => {
    const el = scrollRef.current;
    if (el === null) return;
    const recompute = () => {
      const top = el.getBoundingClientRect().top;
      const available = window.innerHeight - top - 24;
      setScrollHeight(Math.max(MIN_SCROLL_HEIGHT, available));
    };
    recompute();
    const observer = new ResizeObserver(recompute);
    observer.observe(document.body);
    window.addEventListener("resize", recompute);
    return () => {
      observer.disconnect();
      window.removeEventListener("resize", recompute);
    };
  }, []);

  // eslint-disable-next-line react-hooks/incompatible-library -- TanStack Virtual returns functions the React Compiler cannot memoize; this is the library's documented, supported usage and safe here (the virtualizer is used inline, not passed to a memoized child).
  const rowVirtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 10,
  });

  return (
    <div
      ref={scrollRef}
      role="list"
      style={{ height: `${scrollHeight.toString()}px` }}
      className="overflow-y-auto"
    >
      <div
        className="relative w-full"
        style={{ height: `${rowVirtualizer.getTotalSize().toString()}px` }}
      >
        {rowVirtualizer.getVirtualItems().map((virtualRow) => {
          const row = rows[virtualRow.index];
          return (
            <div
              key={rowKey(row, virtualRow.index)}
              role="listitem"
              className="absolute left-0 w-full border-b border-border px-1 last:border-b-0"
              style={{
                height: `${virtualRow.size.toString()}px`,
                transform: `translateY(${virtualRow.start.toString()}px)`,
              }}
            >
              {renderRow(row)}
            </div>
          );
        })}
      </div>
    </div>
  );
}

/**
 * One section's body: branches loading / error / empty / populated as DISTINCT outcomes (an outage renders the
 * WHISPARR_UNAVAILABLE_COPY banner, never the empty copy — the load-bearing error≠empty contract). Populated
 * renders the virtualized rows plus a "Showing n of total — Load more" footer while more pages remain.
 */
function ActivitySection<T>({
  state,
  emptyHeading,
  emptyBody,
  renderRow,
  rowKey,
  onLoadMore,
  onRefresh,
}: {
  state: PagedActivityState<T>;
  emptyHeading: string;
  emptyBody: string;
  renderRow: (row: T) => ReactNode;
  rowKey: (row: T, index: number) => string;
  onLoadMore: () => void;
  onRefresh: () => void;
}) {
  const status = activityStatus(state);

  if (status === "loading") {
    return (
      <div className="flex items-center gap-2">
        <Spinner />
        <StatusText kind="muted">Loading from Whisparr…</StatusText>
      </div>
    );
  }

  if (status === "error") {
    return (
      <div className="flex items-start gap-3 rounded-2xl border border-red-500/40 bg-red-500/10 px-4 py-3">
        <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-red-400" />
        <div className="flex min-w-0 flex-1 flex-col items-start gap-2">
          <StatusText kind="error">{WHISPARR_UNAVAILABLE_COPY}</StatusText>
          <Button variant="ghost" onClick={onRefresh}>
            <RefreshCw className="h-4 w-4" />
            Refresh
          </Button>
        </div>
      </div>
    );
  }

  if (status === "empty") {
    return (
      <div className="space-y-1">
        <p className="text-sm font-semibold text-foreground">{emptyHeading}</p>
        <p className="text-sm text-secondary">{emptyBody}</p>
      </div>
    );
  }

  const rows = state.rows ?? [];
  const hasMore = rows.length < state.total;
  return (
    <div className="space-y-3">
      <VirtualRows rows={rows} renderRow={renderRow} rowKey={rowKey} />
      <div className="flex items-center justify-between gap-3 text-xs text-secondary">
        <span className="tabular-nums">{showingLabel(rows.length, state.total)}</span>
        {hasMore ? (
          <Button variant="ghost" onClick={onLoadMore} disabled={state.loadingMore}>
            {state.loadingMore ? <Spinner /> : null}
            Load more
          </Button>
        ) : null}
      </div>
    </div>
  );
}

/**
 * One section, assembled from its descriptor: the copy comes from the table, the row half from `ROW_VIEWS`,
 * and the paged state from the store. Generic in the key so TypeScript ties all three to the SAME section — an
 * arm rendering another section's rows would not compile.
 */
function ActiveSection<K extends ActivityTab>({
  tab,
  descriptor,
  state,
  onRefresh,
}: {
  /** The descriptor's own key, taken as a type parameter so the state and the row half correlate. */
  tab: K;
  descriptor: ActivitySectionDescriptor;
  state: PagedActivityState<ActivityRowByKey[K]>;
  onRefresh: () => void;
}) {
  const views = ROW_VIEWS[tab];
  return (
    <ActivitySection<ActivityRowByKey[K]>
      state={state}
      emptyHeading={descriptor.emptyHeading}
      emptyBody={descriptor.emptyBody}
      renderRow={views.renderRow}
      rowKey={views.rowKey}
      onLoadMore={() => {
        void loadMoreSection(tab);
      }}
      onRefresh={onRefresh}
    />
  );
}

/** Each section's count badge, or null while it is loading, errored or unfetched — a null count hides it. */
function sectionCounts(states: ActivityStates): Partial<Record<ActivityTab, number | null>> {
  const counts: Partial<Record<ActivityTab, number | null>> = {};
  for (const section of ACTIVITY_SECTIONS) {
    const state = states[section.key];
    counts[section.key] = state.rows !== null && !state.error ? state.total : null;
  }
  return counts;
}

/** The activity sections restore from the URL hash (`#wanted`/`#queue`/`#history`), defaulting to Wanted. */
function isActivityTab(value: string): value is ActivityTab {
  return (ACTIVITY_TAB_ORDER as readonly string[]).includes(value);
}

function readTabFromHash(): ActivityTab {
  const raw = window.location.hash.replace(/^#/, "");
  return isActivityTab(raw) ? raw : "wanted";
}

export function WhisparrDiscoveryPage() {
  const [active, setActive] = useState<ActivityTab>(readTabFromHash);

  const states = useActivityStates();

  // The Queue is the one continuously-changing set — poll it only while its tab is active + visible.
  useQueuePoll(active === "queue");

  const onSelect = useCallback((tab: ActivityTab) => {
    setActive(tab);
    const url = window.location.pathname + window.location.search + `#${tab}`;
    window.history.replaceState(window.history.state, "", url);
  }, []);

  const onRefresh = useCallback(() => {
    void refreshAll();
  }, []);

  const counts = sectionCounts(states);

  const anyLoading = ACTIVITY_SECTIONS.some((section) => states[section.key].loading);

  return (
    <div className="space-y-6">
      <header className="flex items-start justify-between gap-4">
        <div className="flex min-w-0 items-start gap-3">
          <WhisparrLogo className="mt-0.5 h-6 w-6 shrink-0 text-accent" />
          <div className="min-w-0">
            <h2 className="text-base font-semibold text-foreground">Wanted, queue &amp; history</h2>
            <p className="mt-1 text-sm text-secondary">
              What Whisparr is tracking, downloading, and has recently brought in — read-only.
            </p>
          </div>
        </div>
        <Button variant="ghost" onClick={onRefresh} disabled={anyLoading}>
          <RefreshCw className="h-4 w-4" />
          Refresh
        </Button>
      </header>

      <WhisparrActivityTabs active={active} onSelect={onSelect} counts={counts} />

      <div id="activity-panel" role="tabpanel" aria-labelledby={`activity-tab-${active}`}>
        <SectionCard>
          {ACTIVITY_SECTIONS.map((section) =>
            // Keyed on the tab so switching tabs mounts a fresh scroll region, as three sibling branches did.
            section.key === active ? (
              <ActiveSection
                key={section.key}
                tab={section.key}
                descriptor={section}
                state={states[section.key]}
                onRefresh={onRefresh}
              />
            ) : null,
          )}
        </SectionCard>
      </div>
    </div>
  );
}
