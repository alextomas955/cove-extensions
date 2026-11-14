import { SectionCard, Spinner, StatusText } from "@cove-extensions/ui-shared";

import { useCapabilities } from "./useCapabilities";

/**
 * What the host did not supply this extension at load.
 *
 * It renders nothing when the host supplied everything, because a row reading "available" is a
 * diagnostic rather than something a person came here to do. It appears only while the read is in
 * flight, when the read failed, or when a service the run needs is missing.
 */
export function CapabilitiesCard() {
  const { view, loading, error } = useCapabilities();

  if (loading) {
    return (
      <SectionCard title="Scan and Identify">
        <div className="flex items-center gap-2 text-sm text-secondary">
          <Spinner />
          Reading what the host supplied…
        </div>
      </SectionCard>
    );
  }

  if (error !== null) {
    return (
      <SectionCard title="Scan and Identify">
        <StatusText kind="error">Couldn&apos;t read the host capabilities: {error}</StatusText>
      </SectionCard>
    );
  }

  if (view === null) return null;

  const missing = [
    view.jobService ? null : "the job service",
    view.scanService ? null : "the scan service",
  ].filter((name): name is string => name !== null);

  if (missing.length === 0) return null;

  return (
    <SectionCard title="Scan and Identify">
      <StatusText kind="warning">
        This Cove did not supply {missing.join(" or ")}, so a run cannot start. Cove reports version{" "}
        {view.coveVersion ?? "nothing"}.
      </StatusText>
    </SectionCard>
  );
}
