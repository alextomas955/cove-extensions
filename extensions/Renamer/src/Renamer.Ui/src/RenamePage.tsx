/**
 * RenamePage — the component the host mounts inside the dedicated "Rename" SETTINGS TAB
 * (Settings → Extensions → Rename). It renders ONLY the shared {@link RenamePanelBody}: the host
 * already supplies the tab header (title + description from the manifest) and a section card around
 * this component, so adding our own page header/gutter here would triple the "Rename" title. No outer
 * <h1>, no page padding — the body's own card headers ("Filename", "Live preview") provide hierarchy.
 *
 * The host passes `{ onNavigate }` to the component; this UI does not navigate, so it ignores it.
 * Styling uses host Tailwind token classes only (no hex, no CSS bundle).
 */
import { RenamePanelBody } from "./RenameSettingsPanel";

export function RenamePage() {
  return <RenamePanelBody />;
}
