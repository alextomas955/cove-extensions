/**
 * WhisparrFileSettingsSection — the "detect + warn + edit" editor for Whisparr's four file-affecting
 * toggles (§3.7). Because owned-sync is in-place (§6b), these Whisparr settings act on Cove's REAL shared
 * files, so this is one-source-of-truth config editing: turning "Rename movie files" on means Whisparr will
 * rename files in the library Cove points at. The section surfaces an amber in-place-risk warning whenever
 * any toggle is on, and edits write back through the page-level Save bar (the parent posts the four
 * PascalCase booleans to the configure-gated `/file-settings` endpoint, which does the read-modify-write).
 *
 * Presentational: {@link ./SettingsPage} owns the state, the mount read, and the save. A null `settings`
 * (config not readable — no connection) renders the shipped "Test the connection to load this" affordance
 * rather than a guessed all-off state. The editor is v3-only (v2's Sonarr-shaped config fields diverge), so
 * on v2 it renders a version note instead of the toggles.
 */
import { AlertTriangle } from "lucide-react";
import { SectionCard, SectionGroupHeader, StatusText, Toggle } from "@cove-ext/ui-shared";
import { notLoadedMessage } from "./connectionAvailabilityLogic";
import {
  FILE_SETTING_FIELDS,
  FILE_SETTINGS_WARNING_HEADING,
  fileSettingsWarning,
  type FileSettingGroup,
  type FileSettings,
} from "./fileSettingsLogic";

export interface WhisparrFileSettingsSectionProps {
  /** The four toggles, or null when the config can't be read (no connection) — the not-loaded state. */
  settings: FileSettings | null;
  /** Whether the connected version exposes this editor (v3 only). On v2 the section shows a version note. */
  versionSupported: boolean;
  /** A saved connection that's currently unreachable — the not-loaded copy says "retry", not "set up". */
  unreachable: boolean;
  onChange: (settings: FileSettings) => void;
}

const GROUP_TITLES: Record<FileSettingGroup, string> = {
  naming: "Naming",
  mediaManagement: "Media management",
};

const GROUP_ORDER: readonly FileSettingGroup[] = ["naming", "mediaManagement"];

export function WhisparrFileSettingsSection({
  settings,
  versionSupported,
  unreachable,
  onChange,
}: WhisparrFileSettingsSectionProps) {
  const warning = settings ? fileSettingsWarning(settings) : null;

  return (
    <SectionCard
      title="Whisparr file settings"
      description="Whisparr's own naming and folder settings — shown here because in-place sync means they act on Cove's real files."
    >
      {!versionSupported ? (
        <StatusText kind="muted">
          Editing Whisparr&apos;s file settings is currently available on Whisparr v3 (Eros). Your
          v2 instance keeps its own settings; connect a v3 instance to edit them here.
        </StatusText>
      ) : settings === null ? (
        <StatusText kind="muted">
          {notLoadedMessage(unreachable, "Whisparr's file settings")}
        </StatusText>
      ) : (
        <>
          {warning ? (
            <div
              role="status"
              className="flex items-start gap-3 rounded-xl border border-amber-500/40 bg-amber-500/10 px-3 py-2.5"
            >
              <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-400" />
              <div className="min-w-0 space-y-1">
                <p className="text-sm font-semibold text-foreground">
                  {FILE_SETTINGS_WARNING_HEADING}
                </p>
                <p className="text-sm text-secondary">{warning}</p>
              </div>
            </div>
          ) : null}

          {GROUP_ORDER.map((group) => (
            <div key={group} className="space-y-3">
              <SectionGroupHeader title={GROUP_TITLES[group]} />
              {FILE_SETTING_FIELDS.filter((f) => f.group === group).map((f) => (
                <div
                  key={f.key}
                  className="flex items-center justify-between gap-3 rounded-lg border border-border p-3"
                >
                  <div className="min-w-0">
                    <p className="text-sm text-foreground">{f.label}</p>
                    <p className="mt-0.5 text-xs text-secondary">{f.risk}</p>
                  </div>
                  <div className="shrink-0">
                    <Toggle
                      label=""
                      ariaLabel={f.label}
                      checked={settings[f.key]}
                      onChange={(v) => {
                        onChange({ ...settings, [f.key]: v });
                      }}
                    />
                  </div>
                </div>
              ))}
            </div>
          ))}
        </>
      )}
    </SectionCard>
  );
}
