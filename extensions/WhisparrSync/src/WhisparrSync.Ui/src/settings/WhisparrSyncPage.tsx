import { SectionCard } from "@cove-extensions/ui-shared";

/**
 * The component the host mounts inside the "Whisparr Sync" settings tab.
 *
 * The tab uses the host's page layout, so the host draws the tab header from the manifest and no
 * card chrome around this component: the card below is the extension's own. No outer page heading
 * and no page gutter, or the tab name would be drawn twice.
 *
 * The host passes `{ onNavigate }`; this surface does not navigate and ignores it. Styling is host
 * Tailwind token classes only, because the host's Tailwind JIT never scans this bundle.
 */
export function WhisparrSyncPage() {
  return (
    <SectionCard title="Whisparr Sync">
      <p className="text-sm text-secondary">
        Connection setup for Whisparr Sync arrives in a later release.
      </p>
    </SectionCard>
  );
}
