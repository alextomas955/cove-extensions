import { SectionCard, Spinner, StatusText } from "@cove-extensions/ui-shared";

import { useCapabilities } from "./useCapabilities";

function Row({ label, value }: Readonly<{ label: string; value: string }>) {
  return (
    <div className="flex gap-2 text-sm">
      <dt className="text-secondary">{label}</dt>
      <dd className="text-foreground">{value}</dd>
    </div>
  );
}

/** The settings section the C# manifest names, and the bundle's one registered component. */
export function ScanAndIdentifyPage() {
  const { view, loading, error } = useCapabilities();

  return (
    <SectionCard
      title="Scan and Identify"
      description="Finds library items Cove holds no metadata for and identifies them. Nothing scans yet: this panel reports what the host supplied when the extension loaded."
    >
      {loading ? (
        <div className="flex items-center gap-2 text-sm text-secondary">
          <Spinner />
          Reading what the host supplied…
        </div>
      ) : null}

      {error === null ? null : (
        <StatusText kind="error">Couldn&apos;t read the host capabilities: {error}</StatusText>
      )}

      {view === null ? null : (
        <dl className="space-y-1">
          <Row label="Cove version" value={view.coveVersion ?? "not reported"} />
          <Row label="Job service" value={view.jobService ? "available" : "unavailable"} />
          <Row label="Scan service" value={view.scanService ? "available" : "unavailable"} />
        </dl>
      )}
    </SectionCard>
  );
}
