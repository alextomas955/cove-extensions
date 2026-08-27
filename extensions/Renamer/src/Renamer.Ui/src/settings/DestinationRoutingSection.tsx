/**
 * DestinationRoutingSection — the "Destination routing" card: the unorganized destination, the
 * per-studio and per-tag destination maps, advanced allowed-roots + source-path rules, the sidecar-
 * extension list, and the empty-folder cleanup toggle.
 *
 * Card ORDER is presentation only (unorganized first, then per-studio, per-tag, advanced,
 * then sidecar and empty-folder); it does NOT set the engine's rule-evaluation precedence, which is
 * decided server-side, so reordering these cards is safe. Presentational — every field flows up
 * through `set`.
 */
import { useState } from "react";

import {
  NO_DESTINATION,
  toStringKeyed,
  fromStringKeyed,
  type Destination,
  type LibraryPathsState,
  type RenamerOptions,
  type PathDestinationRule,
} from "./options";
import {
  Field,
  TextInput,
  Toggle,
  TagListInput,
  GroupCard,
  SectionCard,
  ToggleHeaderCard,
  KeyValueMapEditor,
  ObjectArrayEditor,
  RegexValidity,
  StatusText,
  extensionShapeAdvisory,
} from "@cove-extensions/ui-shared";
import { EntitySelectField } from "./EntitySelectField";
import { DestinationField } from "./DestinationField";
import { StudioDestinationsEditor } from "./StudioMap";
import { RuleKeyLabel } from "./RuleKeyLabel";
import { useOrphanedRules } from "./useOrphanedRules";

/** Strip one leading dot if present, then lowercase — the add-time transform for a sidecar extension. */
function normalizeSidecarExtension(raw: string): string {
  let v = raw.trim();
  if (v.startsWith(".")) v = v.slice(1);
  return v.toLowerCase();
}

export interface DestinationRoutingSectionProps {
  options: RenamerOptions;
  set: <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => void;
  /** Cove's library paths, so every destination here offers them as choices. */
  library: LibraryPathsState;
}

export function DestinationRoutingSection({
  options,
  set,
  library,
}: DestinationRoutingSectionProps) {
  // Live (not-yet-committed) AssociatedExtensions input, so the sidecar-extension advisory reflects
  // what the user is currently typing, before Enter commits it.
  const [sidecarLiveInput, setSidecarLiveInput] = useState("");
  const orphaned = useOrphanedRules();

  return (
    <SectionCard description="Per-studio and per-tag rules override the default.">
      <GroupCard
        title="Unorganized destination"
        description="Where un-curated items go instead of being skipped."
      >
        <Toggle
          label="Route unorganized items to their own destination"
          checked={options.UnorganizedDestination !== null}
          onChange={(on) => {
            // Off is the ABSENT destination, not one naming nothing: only the absent one falls
            // through to the only-organized gate, which is what decides whether the item is skipped.
            set("UnorganizedDestination", on ? { ...NO_DESTINATION } : null);
          }}
        />
        {options.UnorganizedDestination === null ? null : (
          <DestinationField
            value={options.UnorganizedDestination}
            onChange={(destination) => {
              set("UnorganizedDestination", destination);
            }}
            library={library}
          />
        )}
      </GroupCard>

      <ToggleHeaderCard
        title="Per-studio destinations"
        description="Route a studio's items to their own destination."
        enabled={options.EnableStudioDestinations}
        onToggle={(v) => {
          set("EnableStudioDestinations", v);
        }}
      >
        <StudioDestinationsEditor
          map={options.StudioDestinations}
          onChange={(m) => {
            set("StudioDestinations", m);
          }}
          library={library}
        />
      </ToggleHeaderCard>

      <ToggleHeaderCard
        title="Per-tag destinations"
        description="Route a tag's items to their own destination."
        enabled={options.EnableTagDestinations}
        onToggle={(v) => {
          set("EnableTagDestinations", v);
        }}
      >
        {/* The tag id must stay a NUMBER end to end to keep the persisted map value-equal with the
            backend field and normalizeOptions, so it crosses the string-keyed KeyValueMapEditor
            boundary through the same explicit coercion the studio map uses. The host resolves a
            committed row's opaque id to the tag's name: one cached lookup per configured rule,
            bounded by the rules the user authored rather than by the library. */}
        <KeyValueMapEditor<Destination>
          map={toStringKeyed(options.TagDestinations)}
          onChange={(m) => {
            set("TagDestinations", fromStringKeyed(m));
          }}
          emptyValue={NO_DESTINATION}
          renderKey={(draftKey, setDraftKey, existingKeys) => (
            <EntitySelectField
              entityType="tag"
              label="Tag"
              values={draftKey === "" ? [] : [Number(draftKey)]}
              onChange={(values) => {
                // Last-id-wins: the selector is multi-value but a map key holds exactly one tag.
                const latest = values.at(-1);
                setDraftKey(latest === undefined ? "" : String(latest));
              }}
              placeholder="Search tags…"
              excludeIds={existingKeys.map(Number)}
            />
          )}
          renderValue={(value, setValue) => (
            <DestinationField value={value} onChange={setValue} library={library} />
          )}
          renderKeyLabel={(key) => (
            <RuleKeyLabel
              entityType="tag"
              id={Number(key)}
              orphaned={orphaned.tags.has(Number(key))}
            />
          )}
          addLabel="Add tag rule"
        />
      </ToggleHeaderCard>

      <ToggleHeaderCard
        title="Advanced routing & safety"
        description="Allowed roots and source-path rules."
        enabled={options.EnableAdvancedRouting}
        onToggle={(v) => {
          set("EnableAdvancedRouting", v);
        }}
      >
        <div>
          <h4 className="text-sm font-semibold text-foreground">Allowed roots</h4>
          <p className="mb-4 mt-1 text-sm text-secondary">
            An optional narrowing: when set, a rename may only write inside these absolute
            directories, even when a destination allows more. Empty = no narrowing.
          </p>
          <TagListInput
            values={options.AllowedRoots}
            onChange={(v) => {
              set("AllowedRoots", v);
            }}
            placeholder="Add an absolute directory, press Enter"
          />
        </div>

        <div>
          <h4 className="text-sm font-semibold text-foreground">Source-path destinations</h4>
          <p className="mb-4 mt-1 text-sm text-secondary">
            Match an item&apos;s source path to a destination, top rule first. An exact match or a
            regex.
          </p>
          <ObjectArrayEditor<PathDestinationRule>
            rows={options.PathDestinations}
            onChange={(rows) => {
              set("PathDestinations", rows);
            }}
            makeRow={() => ({ Pattern: "", Dest: { ...NO_DESTINATION }, IsRegex: false })}
            renderRow={(row, _i, update) => (
              <>
                <Field label="Source path">
                  <TextInput
                    value={row.Pattern}
                    onChange={(v) => {
                      update({ Pattern: v });
                    }}
                    mono
                    placeholder="Exact path or regex"
                  />
                </Field>
                <Toggle
                  label="Match as a regex"
                  checked={row.IsRegex}
                  onChange={(v) => {
                    update({ IsRegex: v });
                  }}
                />
                <RegexValidity pattern={row.Pattern} isRegex={row.IsRegex} />
                <DestinationField
                  value={row.Dest}
                  onChange={(destination) => {
                    update({ Dest: destination });
                  }}
                  library={library}
                />
              </>
            )}
            addLabel="Add path rule"
            ordered
          />
        </div>
      </ToggleHeaderCard>

      <GroupCard
        title="Sidecar files"
        description="A file sharing the primary's basename moves and renames with it; an existing target is never overwritten. Cove-tracked captions always move."
      >
        <Field label="Also move sidecar files with these extensions">
          <TagListInput
            values={options.AssociatedExtensions}
            onChange={(v) => {
              set("AssociatedExtensions", v);
            }}
            placeholder="Add an extension, press Enter"
            normalize={normalizeSidecarExtension}
            onReject={(candidate) => !/^[a-z0-9]+$/.test(candidate)}
            onLiveChange={(raw) => {
              setSidecarLiveInput(raw);
            }}
          />
          {(() => {
            const advisory = extensionShapeAdvisory(normalizeSidecarExtension(sidecarLiveInput));
            return advisory ? <StatusText kind="warning">{advisory}</StatusText> : null;
          })()}
        </Field>
      </GroupCard>

      <div className="rounded-xl border border-border bg-card p-4">
        <Toggle
          label="Delete the source folder when a move leaves it empty"
          checked={options.RemoveEmptyFolder}
          onChange={(v) => {
            set("RemoveEmptyFolder", v);
          }}
          helper="Never a non-empty folder or a root. Undo won't recreate it."
        />
      </div>
    </SectionCard>
  );
}
