/**
 * SettingsPage — the component the host mounts inside the dedicated "Whisparr Sync" SETTINGS TAB
 * (Settings → Extensions → Whisparr Sync). It renders ONLY the {@link ConnectionSettingsPanel}: the
 * host already supplies the tab header (title + description from the manifest) and the surrounding
 * chrome, so no outer heading/gutter is added here.
 *
 * The host passes `{ onNavigate }`; this UI does not navigate, so it ignores it. Styling uses host
 * Tailwind token classes only (no hex, no CSS bundle).
 */
import { ConnectionSettingsPanel } from "./ConnectionSettingsPanel";

export function SettingsPage() {
  return <ConnectionSettingsPanel />;
}
