/**
 * What a panel says back to the user: the inline validity notes, the badges and status pills, the
 * spinner and the progress track.
 *
 * A colour here is never the only carrier of meaning - every pill takes an icon, and every status
 * line is words. Where the host's prebuilt stylesheet emits no utility for a tint, the tint is an
 * inline style over the host's own custom properties rather than an arbitrary Tailwind value, which
 * would resolve to nothing: the host's JIT never scans this bundle.
 */
import type { CSSProperties, ReactNode } from "react";
import { Loader2 } from "lucide-react";

import { isAbsolutePathShape, isRegexValid } from "./primitivesLogic";

/**
 * Inline regex-validity message for a rule row. Presentational only - it shows an error when the
 * pattern is in regex mode and obviously malformed, and renders nothing otherwise; the consuming row
 * decides whether to block on it. The verdict comes from the tested {@link isRegexValid}, whose
 * browser-vs-.NET caveat applies: this flags obvious JS parse errors, not full .NET parity, so a
 * clean result is not a promise the engine will accept the pattern.
 */
export function RegexValidity({ pattern, isRegex }: { pattern: string; isRegex: boolean }) {
  if (!isRegex) return null;
  const result = isRegexValid(pattern);
  if (result.valid) return null;
  return <StatusText kind="error">Invalid pattern: {result.message}</StatusText>;
}

/**
 * Advisory-only, non-blocking hint that a field meant to hold a relative folder template has been
 * given something that looks like a typed path. Mirrors {@link RegexValidity}'s presentational shape
 * exactly: pure, stateless, and renders nothing when the value looks fine or is blank.
 *
 * A typed path is not refused - it renders as ordinary folder names under whichever root the field's
 * owner chose, which is confined and harmless but almost never what the author meant.
 *
 * `message` is the remedy, and it is the caller's because only the caller knows what it is. Wording
 * it here would put one extension's panel layout inside a business-agnostic package.
 */
export function PathShapeHint({ value, message }: { value: string; message: string }) {
  if (value.trim().length === 0) return null;
  if (!isAbsolutePathShape(value)) return null;
  return <StatusText kind="warning">{message}</StatusText>;
}

/**
 * A small accent-tinted pill, used for the token-settings `$token` badges. `mono` renders the
 * label monospaced (the `$token` case); the plain case is the short uppercase tag. Host-compiled
 * classes only.
 */
export function Badge({ children, mono = false }: { children: ReactNode; mono?: boolean }) {
  const base = "inline-flex items-center rounded-md px-2 py-0.5 text-xs font-semibold";
  const color = "border border-accent/40 bg-accent/15 text-accent";
  return (
    <span className={`${base} ${color} ${mono ? "font-mono" : "uppercase tracking-wider"}`}>
      {children}
    </span>
  );
}

export type StatusPillVariant = "accent" | "amber" | "red" | "green" | "gray";

/** A theme color at partial alpha, in the same form the host's own stylesheet emits. */
const tint = (variable: string, percent: number) =>
  `color-mix(in oklab, var(${variable}) ${percent}%, transparent)`;

// A utility the host's prebuilt stylesheet never emits contributes no declaration at all, and no
// error anywhere, because the host Tailwind JIT does not scan this bundle. The pill still renders: a
// missing fill leaves it transparent, and a missing border color falls back to currentColor, so the
// outline picks up the text color at full strength. Cove defines the color scale as custom
// properties even for the utilities it never emits, so a `style` follows the host theme rather than
// freezing a literal. Anything the host does emit stays a class; measure a candidate against the
// floor `extension.json` declares before inlining it, and list the host-absent ones in
// check-classes. A consumer whose text color rides on inner spans overrides the `text-*` class.
const STATUS_PILL_VARIANT: Record<StatusPillVariant, { className: string; style?: CSSProperties }> =
  {
    accent: { className: "border-accent/40 bg-accent/10 text-accent" },
    amber: {
      className: "text-amber-400",
      style: {
        borderColor: tint("--color-amber-400", 40),
        backgroundColor: tint("--color-amber-400", 10),
      },
    },
    red: {
      className: "border-red-700/50 text-red-400",
      style: { backgroundColor: tint("--color-red-950", 40) },
    },
    green: {
      className: "text-green-400",
      style: {
        borderColor: tint("--color-green-500", 40),
        backgroundColor: tint("--color-green-500", 10),
      },
    },
    gray: { className: "border-border bg-card text-muted" },
  };

/**
 * A status-tinted pill - the badge family the single-accent `$token` {@link Badge} does not cover.
 * `variant` selects the tint, `shape` the corner radius (`pill` fully rounded | `tag` `rounded-md`),
 * `icon` a caller-supplied leading glyph so status never rides on color alone.
 */
export function StatusPill({
  variant,
  icon,
  shape = "pill",
  title,
  children,
}: {
  variant: StatusPillVariant;
  icon?: ReactNode;
  shape?: "pill" | "tag";
  title?: string;
  children: ReactNode;
}) {
  // gap tracks shape to keep both families' existing spacing: the fully-rounded pill sits tighter at
  // gap-1, the rounded-md status tags at gap-1.5.
  const gap = shape === "pill" ? "gap-1" : "gap-1.5";
  const rounded = shape === "pill" ? "rounded-full" : "rounded-md";
  const tone = STATUS_PILL_VARIANT[variant];
  return (
    <span
      title={title}
      className={`inline-flex items-center ${gap} ${rounded} border px-2 py-0.5 text-xs ${tone.className}`}
      style={tone.style}
    >
      {icon}
      {children}
    </span>
  );
}

export type StatusKind = "success" | "error" | "muted" | "warning";

const STATUS_CLASS: Readonly<Record<StatusKind, string>> = {
  success: "text-green-400",
  error: "text-red-400",
  warning: "text-amber-400",
  muted: "text-secondary",
};

export function StatusText({ kind, children }: { kind: StatusKind; children: ReactNode }) {
  return <span className={`text-xs ${STATUS_CLASS[kind]}`}>{children}</span>;
}

export function Spinner() {
  return <Loader2 className="h-4 w-4 animate-spin" />;
}

/**
 * A thin determinate/indeterminate progress track. Presentational: the caller supplies an already-
 * clamped whole `percent` (0..100) when it has one, or omits it for the indeterminate state before
 * the first sample arrives. The fill width is an element-scoped inline style - an arbitrary
 * `w-[NN%]` class would render nothing here (the host's Tailwind JIT never scans this bundle, the
 * same reason {@link Toggle}'s knob offset is inline). Colors/shape come from host-emitted utilities
 * only, so no CSS ships. Determinate carries `role="progressbar"` + `aria-valuenow/min/max`;
 * indeterminate carries `aria-busy` instead, with a full-width pulsing fill.
 */
export function ProgressBar({ percent, label }: { percent?: number; label?: string }) {
  const determinate = percent !== undefined;
  return (
    // A native <progress> draws its fill through vendor pseudo-elements, which host utility classes
    // cannot reach.
    <div // NOSONAR
      role="progressbar"
      aria-label={label}
      aria-valuenow={determinate ? percent : undefined}
      aria-valuemin={determinate ? 0 : undefined}
      aria-valuemax={determinate ? 100 : undefined}
      aria-busy={determinate ? undefined : true}
      className="h-1.5 w-full overflow-hidden rounded-full bg-card"
    >
      <div
        className={`h-full rounded-full bg-accent ${determinate ? "transition-all" : "animate-pulse"}`}
        style={{ width: determinate ? `${percent}%` : "100%" }}
      />
    </div>
  );
}
