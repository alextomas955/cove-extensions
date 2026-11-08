/**
 * The containers a settings page is stacked from: the page-level card, the blocks inside one, and
 * the two that reveal their body on a toggle or a click.
 *
 * Cove's own `SettingsSection` and `CollapsibleSection` are not importable from an extension bundle,
 * so these match their classes rather than wrapping them. Presentational, except for
 * {@link CollapsibleSection}, which owns whether it is open.
 */
import type { ReactNode } from "react";
import { useId, useState } from "react";
import { ChevronDown, ChevronUp } from "lucide-react";

import { Toggle } from "./fields";

/** One of the five field groups. Matches Cove sub-card styling. */
export function GroupCard({
  title,
  description,
  badge,
  headerRight,
  children,
}: {
  title: string;
  description?: string;
  badge?: ReactNode;
  headerRight?: ReactNode;
  children: ReactNode;
}) {
  return (
    <div className="rounded-xl border border-border bg-card p-4">
      <div className="flex items-start justify-between gap-4">
        <div className="flex min-w-0 items-start gap-2">
          {badge ? <span className="mt-0.5 shrink-0">{badge}</span> : null}
          <h3 className="text-base font-semibold text-foreground">{title}</h3>
        </div>
        {headerRight ? <div className="shrink-0">{headerRight}</div> : null}
      </div>
      {description ? (
        <p className="mb-4 mt-1 text-sm text-secondary">{description}</p>
      ) : (
        <div className="mb-4" />
      )}
      <div className="space-y-4">{children}</div>
    </div>
  );
}

/**
 * A titled block inside a {@link SectionCard}, for a card that holds more than one subject. The
 * `<section>` is named through `aria-labelledby`, so it exposes a `region` an assistive-technology
 * user (and a test locator) can address by its title - the enclosing card's own heading is not wired
 * that way, which is what keeps a nested block unambiguous. `divided` draws the hairline that
 * separates it from the block above.
 */
export function CardSection({
  title,
  description,
  divided = false,
  children,
}: {
  title: string;
  description?: string;
  divided?: boolean;
  children: ReactNode;
}) {
  const headingId = useId();
  return (
    <section
      aria-labelledby={headingId}
      className={divided ? "border-t border-border pt-4" : undefined}
    >
      <h4 id={headingId} className="text-base font-semibold text-foreground">
        {title}
      </h4>
      {description ? <p className="mt-1 text-sm text-secondary">{description}</p> : null}
      <div className="mt-4 space-y-4">{children}</div>
    </section>
  );
}

/**
 * The primary settings section container: Cove's own `SettingsSection` fill
 * (`rounded-2xl border-border bg-surface p-5` plus a long soft drop shadow) under a header that
 * closes with a hairline rule. The panel is a full page of stacked titled cards rather than one
 * embedded settings section, and the rule is what makes a card title read as a header instead of as
 * the body's first line. `badge` is the one addition core lacks: an inline `$token` marker for the
 * token-settings cards. Presentational only.
 */
export function SectionCard({
  title,
  description,
  badge,
  headerRight,
  children,
}: {
  title?: string;
  description?: string;
  badge?: ReactNode;
  headerRight?: ReactNode;
  children: ReactNode;
}) {
  const hasHeader = Boolean(title) || Boolean(description) || badge != null || headerRight != null;
  return (
    <section
      className="rounded-2xl border border-border bg-surface p-5"
      // The host Tailwind JIT never scans this bundle, so an arbitrary `shadow-[…]` class renders nothing;
      // the computed shadow goes in an inline style (the same rule as the ProgressBar fill / Toggle knob).
      style={{ boxShadow: "0 12px 30px -20px rgba(0,0,0,0.7)" }}
    >
      {hasHeader ? (
        <header className="mb-4 flex items-start justify-between gap-4 border-b border-border pb-4">
          <div className="flex min-w-0 items-start gap-3">
            {badge ? <span className="mt-0.5 shrink-0">{badge}</span> : null}
            <div className="min-w-0">
              {title ? <h3 className="text-base font-semibold text-foreground">{title}</h3> : null}
              {description ? <p className="mt-1 text-sm text-secondary">{description}</p> : null}
            </div>
          </div>
          {headerRight ? <div className="shrink-0">{headerRight}</div> : null}
        </header>
      ) : null}
      <div className="space-y-4">{children}</div>
    </section>
  );
}

/**
 * A card whose header carries an enable {@link Toggle}; the body renders inline only when `enabled`.
 * The design's per-studio / per-tag / advanced-routing pattern - the card stays visible with its
 * title + description, and flipping the header toggle reveals the controls (or an off-state hint).
 * The toggle state is owned by the caller (the same Enable* option), so this is presentational.
 */
export function ToggleHeaderCard({
  title,
  description,
  enabled,
  onToggle,
  children,
}: {
  title: string;
  description?: string;
  enabled: boolean;
  onToggle: (enabled: boolean) => void;
  children: ReactNode;
}) {
  return (
    <section className="overflow-hidden rounded-xl border border-border bg-card">
      <div className="flex items-center gap-3 px-4 py-3">
        <div className="min-w-0 flex-1">
          <h3 className="text-base font-semibold text-foreground">{title}</h3>
          {description ? <p className="mt-1 text-sm text-secondary">{description}</p> : null}
        </div>
        <div className="shrink-0">
          <Toggle label="" ariaLabel={`Enable ${title}`} checked={enabled} onChange={onToggle} />
        </div>
      </div>
      {enabled ? (
        <div className="space-y-4 border-t border-border px-4 pb-4 pt-4">{children}</div>
      ) : null}
    </section>
  );
}

/**
 * Progressive-disclosure section (mirrors Cove's native `CollapsibleSection`, which isn't importable
 * from an extension bundle). Collapsed by default; the header always shows the title + a one-line
 * `summary` of what's inside so a closed section still telegraphs its contents (NN/g accordion rule).
 * Self-manages open state. Host-compiled classes only.
 */
export function CollapsibleSection({
  title,
  summary,
  defaultOpen = false,
  children,
}: {
  title: string;
  summary?: string;
  defaultOpen?: boolean;
  children: ReactNode;
}) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <div className="overflow-hidden rounded-xl border border-border">
      <button
        type="button"
        onClick={() => {
          setOpen((v) => !v);
        }}
        aria-expanded={open}
        className="flex w-full items-center justify-between gap-4 bg-card px-4 py-3 text-left transition-colors hover:bg-card-hover"
      >
        <span className="min-w-0">
          <span className="block text-sm font-medium text-foreground">{title}</span>
          {summary ? (
            <span className="mt-1 block truncate text-xs text-muted">{summary}</span>
          ) : null}
        </span>
        {open ? (
          <ChevronUp className="h-4 w-4 shrink-0 text-muted" />
        ) : (
          <ChevronDown className="h-4 w-4 shrink-0 text-muted" />
        )}
      </button>
      {open ? <div className="space-y-4 border-t border-border px-4 py-3">{children}</div> : null}
    </div>
  );
}
