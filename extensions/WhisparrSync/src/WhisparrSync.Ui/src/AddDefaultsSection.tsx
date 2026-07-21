/**
 * AddDefaultsSection — the "Add defaults" SectionCard: the defaults applied when Whisparr adds
 * an item. Quality profile (populated from the live API after a successful test), Tags on add (chips, default
 * `cove`), Monitor new by default, and Allow quality upgrades — all round-tripped through the /options wire.
 * Presentational — {@link ./SettingsPage} owns the state + the single save. There is no root-folder setting:
 * the add's root is derived per-add server-side from Whisparr's own root list.
 *
 * "Search on add" is rendered as an explicitly DISABLED control with a one-sentence reason: Cove keeps adds
 * search-free so an add can never kick off a grab loop (loop-safety is LOCKED, and it is deliberately left
 * off the options wire). It is a documented, visible fallback — never a silent omission.
 */
import type { ReactNode } from "react";
import { Field, SectionCard, Select, StatusText, TagListInput, Toggle } from "@cove-ext/ui-shared";
import { notLoadedMessage } from "./connectionAvailabilityLogic";

/** A read-only switch affordance for a control that is intentionally not wired (Search on add). */
function DisabledToggle({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <div className="flex items-center gap-2 text-sm text-secondary opacity-60">
        <span
          role="switch"
          aria-checked={false}
          aria-disabled
          className="inline-flex h-5 w-9 items-center rounded-full border border-border bg-card"
        >
          <span
            className="inline-block h-4 w-4 rounded-full bg-white"
            style={{ transform: "translateX(0.125rem)" }}
          />
        </span>
        <span>{label}</span>
      </div>
      <p className="mt-1 text-xs text-secondary">{children}</p>
    </div>
  );
}

export interface AddDefaultsSectionProps {
  qualityProfileId: number;
  onQualityProfile: (id: number) => void;
  qualityProfileOptions: readonly { value: string; label: string }[];
  listsLoaded: boolean;
  tags: string[];
  onTags: (tags: string[]) => void;
  monitorNew: boolean;
  onMonitorNew: (value: boolean) => void;
  allowUpgrades: boolean;
  onAllowUpgrades: (value: boolean) => void;
  /** Whether the connected version has a cutoff-upgrade search (v3 only). On v2 the toggle is shown disabled with a reason rather than pretending it does something. */
  upgradesSupported: boolean;
  /** A saved connection that's currently unreachable — the not-loaded copy says "retry", not "set up". */
  unreachable: boolean;
}

export function AddDefaultsSection({
  qualityProfileId,
  onQualityProfile,
  qualityProfileOptions,
  listsLoaded,
  tags,
  onTags,
  monitorNew,
  onMonitorNew,
  allowUpgrades,
  onAllowUpgrades,
  upgradesSupported,
  unreachable,
}: AddDefaultsSectionProps) {
  return (
    <SectionCard
      title="Add defaults"
      description="Applied when you monitor a studio or performer, or add a scene — the actual monitoring lives in Whisparr."
    >
      <Field
        label="Quality profile"
        helper={listsLoaded ? "The quality profile new items are added with." : undefined}
      >
        <Select
          value={String(qualityProfileId)}
          onChange={(v) => {
            onQualityProfile(Number(v));
          }}
          options={qualityProfileOptions}
          disabled={!listsLoaded}
        />
      </Field>

      <Field
        label="Tags on add"
        helper="Tags applied to what Whisparr adds. Keep `cove` so reconciliation can recognise its own adds."
      >
        <TagListInput values={tags} onChange={onTags} placeholder="Add a tag and press Enter" />
      </Field>

      <Toggle
        label="Monitor new items by default"
        checked={monitorNew}
        onChange={onMonitorNew}
        helper="A monitored item is one Whisparr keeps looking to grab (and upgrade)."
      />

      {upgradesSupported ? (
        <Toggle
          label="Allow quality upgrades"
          checked={allowUpgrades}
          onChange={onAllowUpgrades}
          helper="Let Whisparr replace a grabbed release with a better one, up to the profile cutoff."
        />
      ) : (
        <DisabledToggle label="Allow quality upgrades">
          Whisparr v2 has no cutoff-upgrade search, so this applies only on Whisparr v3 (Eros). Your
          setting is kept and takes effect if you connect a v3 instance.
        </DisabledToggle>
      )}

      <DisabledToggle label="Search on add">
        Cove keeps adds search-free so an add can never start a grab loop. Use Search now (per scene
        or over a selection) when you want Whisparr to go looking.
      </DisabledToggle>

      {!listsLoaded ? (
        <StatusText kind="muted">
          {notLoadedMessage(unreachable, "your quality profiles")}
        </StatusText>
      ) : null}
    </SectionCard>
  );
}
