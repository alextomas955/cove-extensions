import { CapabilitiesCard } from "./CapabilitiesCard";
import { RunOptionsForm } from "./RunOptionsForm";

/** The settings section the C# manifest names, and the bundle's one registered component. */
export function ScanAndIdentifyPage() {
  return (
    <div className="space-y-4">
      <CapabilitiesCard />
      <RunOptionsForm />
    </div>
  );
}
