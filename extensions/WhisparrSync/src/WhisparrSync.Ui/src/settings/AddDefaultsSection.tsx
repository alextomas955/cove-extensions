/**
 * AddDefaultsSection — the "Add defaults" SectionCard: the defaults applied when Whisparr adds
 * an item. Tags on add (chips, default `cove`), Monitor new by default, and Allow quality upgrades — all
 * round-tripped through the /options wire. Presentational — {@link ./SettingsPage} owns the state + the single
 * save. There is no root-folder and no quality-profile setting: both are derived per-add server-side from
 * Whisparr's own root list and profile list.
 *
 * Whether an add searches is Whisparr's own per-studio Search on Add; Cove never asks for one, so the
 * section carries a sentence about it and no control.
 */
import type { ReactNode } from "react";
import { Field, SectionCard, TagListInput, Toggle } from "@cove-extensions/ui-shared";

/** A read-only switch affordance for a toggle this connection cannot honour. */
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
  tags: string[];
  onTags: (tags: string[]) => void;
  monitorNew: boolean;
  onMonitorNew: (value: boolean) => void;
  allowUpgrades: boolean;
  onAllowUpgrades: (value: boolean) => void;
  /** Whether the connected version has a cutoff-upgrade search (v3 only). On v2 the toggle is shown disabled with a reason rather than pretending it does something. */
  upgradesSupported: boolean;
}

export function AddDefaultsSection({
  tags,
  onTags,
  monitorNew,
  onMonitorNew,
  allowUpgrades,
  onAllowUpgrades,
  upgradesSupported,
}: AddDefaultsSectionProps) {
  return (
    <SectionCard
      title="Add defaults"
      description="Applied when the extension adds a studio, performer, or scene to Whisparr — one Whisparr already has keeps its own settings. The monitoring itself lives in Whisparr."
    >
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

      {/* A statement, not a control: whether an add searches is Whisparr's own per-studio "Search on Add",
          and Cove never asks for one because it only adds scenes whose files you already have. Rendering a
          toggle here would imply Cove owns that setting. */}
      <p className="text-sm text-secondary">
        Cove never asks Whisparr to search when it adds a scene — it only adds scenes you already
        have, so a search would fetch a second copy. Whisparr&rsquo;s own <em>Search on Add</em> is
        unaffected for anything you add in Whisparr. Use <strong>Search now</strong> (per scene or
        over a selection) when you want Whisparr to go looking.
      </p>
    </SectionCard>
  );
}
