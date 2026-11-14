import { CapabilitiesCard } from "./CapabilitiesCard";
import { RunOptionsForm } from "./RunOptionsForm";
import { RunSection } from "./RunSection";
import { useRunOptions } from "./useRunOptions";

/**
 * The settings section the C# manifest names, and the bundle's one registered component.
 *
 * The options are read once here and handed to both cards. A run reads what the server holds, so a
 * form with unsaved edits would otherwise start a run that does something other than what the
 * switches on screen show.
 */
export function ScanAndIdentifyPage() {
  const options = useRunOptions();

  return (
    <div className="space-y-4">
      <CapabilitiesCard />
      <RunSection optionsDirty={options.dirty} />
      <RunOptionsForm controller={options} />
    </div>
  );
}
