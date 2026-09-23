import { KeyValueMapEditor } from "@cove-extensions/ui-shared";
import { EntitySelectField } from "./EntitySelectField";
import { DestinationField } from "./DestinationField";
import { NO_DESTINATION, type Destination, type LibraryPathsState } from "./options";
import { RuleKeyLabel } from "./RuleKeyLabel";

const COPY = {
  studio: { label: "Studio", placeholder: "Search studios…", addLabel: "Add studio rule" },
  tag: { label: "Tag", placeholder: "Search tags…", addLabel: "Add tag rule" },
} as const;

/**
 * A per-studio or per-tag destination-rule editor. A rule keys on the entity's stable id and the host
 * resolves each committed id to a name, one cached lookup per rule the user wrote.
 */
export function EntityDestinationsEditor({
  entityType,
  map,
  onChange,
  library,
  orphaned,
}: {
  entityType: "studio" | "tag";
  map: Record<string, Destination>;
  onChange: (map: Record<string, Destination>) => void;
  library: LibraryPathsState;
  /** Rule keys naming an entity Cove no longer holds. */
  orphaned: ReadonlySet<number>;
}) {
  const copy = COPY[entityType];
  return (
    <KeyValueMapEditor<Destination>
      map={map}
      onChange={onChange}
      emptyValue={NO_DESTINATION}
      renderKey={(draftKey, setDraftKey, existingKeys) => (
        <EntitySelectField
          entityType={entityType}
          label={copy.label}
          values={draftKey === "" ? [] : [Number(draftKey)]}
          onChange={(values) => {
            // The selector is multi-value and a map key holds one entity, so the latest pick wins.
            const latest = values.at(-1);
            setDraftKey(latest === undefined ? "" : String(latest));
          }}
          placeholder={copy.placeholder}
          excludeIds={existingKeys.map(Number)}
        />
      )}
      renderValue={(value, setValue) => (
        <DestinationField value={value} onChange={setValue} library={library} />
      )}
      renderKeyLabel={(key) => (
        <RuleKeyLabel
          entityType={entityType}
          id={Number(key)}
          orphaned={orphaned.has(Number(key))}
        />
      )}
      addLabel={copy.addLabel}
    />
  );
}
