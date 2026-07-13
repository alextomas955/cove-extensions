/**
 * SettingsPage — the component the host mounts inside the dedicated "Whisparr Sync" SETTINGS TAB
 * (Settings → Extensions → Whisparr Sync). It stacks the {@link ConnectionSettingsPanel} (connect +
 * setup) and, below it, the {@link ReconciliationSection} (the read-only matched / unmatched /
 * needs-review view). The host already supplies the tab header (title + description from the manifest)
 * and the surrounding chrome, so no outer heading/gutter is added here.
 *
 * The host passes `{ onNavigate }`; this UI does not navigate, so it ignores it. Styling uses host
 * Tailwind token classes only (no hex, no CSS bundle).
 */
import { ConnectionSettingsPanel } from "./ConnectionSettingsPanel";
import { ReconciliationSection } from "./ReconciliationSection";

export function SettingsPage() {
  return (
    <div className="space-y-6">
      <ConnectionSettingsPanel />
      <ReconciliationSection />
    </div>
  );
}
