/**
 * AdvancedSection — the "Advanced" section's panels, all collapsed by default: name cleanup
 * (illegal/space handling, case, ASCII), length & collisions, cross-drive concurrency, the
 * pre-routing excludes, and field rewriting & name shaping. Presentational — every field flows up
 * through `set`.
 */
import { type ReactNode } from "react";

import {
  type RenamerOptions,
  type CaseTransform,
  type ExcludeRule,
  type FieldReplaceRule,
} from "./options";
import {
  Field,
  TextInput,
  NumberInput,
  Select,
  Toggle,
  TagListInput,
  CollapsibleSection,
  SectionCard,
  ObjectArrayEditor,
  RegexValidity,
  SegmentedReplace,
  ExampleSelect,
  type ExampleOption,
} from "@cove-extensions/ui-shared";
import { EntitySelectField } from "./EntitySelectField";
import { BARE_TOKENS } from "./templateValidation";
import { optionsFor } from "./selectOptions";
import { TokenAdvisory } from "./templateAdvisories";

const CASE_OPTIONS = optionsFor<CaseTransform>({
  none: "None",
  lower: "lowercase",
  title: "Title Case",
});

// What a FieldReplaceRule may target: the canonical spelling the backend matches, offered as a closed
// set so a rule cannot target a token the engine never resolves. The names come from the token legend,
// which is where this panel keeps them.
const TOKEN_OPTIONS: readonly { value: string; label: string }[] = BARE_TOKENS.map((token) => ({
  value: token,
  label: token,
}));

// Common duplicate-suffix patterns; {n} = collision counter, shown via example.
const SUFFIX_FORMAT_OPTIONS: readonly ExampleOption[] = [
  { value: " ({n})", example: "name (1).mp4" },
  { value: "_{n}", example: "name_1.mp4" },
  { value: " - {n}", example: "name - 1.mp4" },
];

// A named block inside an Advanced panel, for a block that is not one labelable control. The
// heading is a plain element, not a label: a label forwards a click on its text to the first
// control it wraps, which for a chip list is a chip's Remove button.

function SubBlock({
  heading,
  description,
  children,
}: {
  heading: string;
  description?: string;
  children: ReactNode;
}) {
  return (
    <div>
      <h4 className="text-sm text-secondary">{heading}</h4>
      {description ? <p className="mt-1 text-xs text-secondary">{description}</p> : null}
      <div className="mt-2 space-y-2">{children}</div>
    </div>
  );
}

export interface AdvancedSectionProps {
  options: RenamerOptions;
  set: <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => void;
}

export function AdvancedSection({ options, set }: AdvancedSectionProps) {
  return (
    <SectionCard title="Advanced">
      <CollapsibleSection
        title="Clean up the name"
        summary="Illegal-character and space handling, case, ASCII"
      >
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <Field label="Illegal characters">
            <SegmentedReplace
              value={options.illegalReplacement}
              onChange={(v) => {
                set("illegalReplacement", v);
              }}
              stripLabel="Strip"
              replaceLabel="Replace with"
              stripHelper="Illegal characters are removed."
              replaceHelper="Blank drops them."
              inputPlaceholder="e.g. _"
            />
          </Field>
          <Field label="Space replacement">
            <SegmentedReplace
              value={options.spaceReplacement}
              onChange={(v) => {
                set("spaceReplacement", v);
              }}
              stripLabel="Keep spaces"
              replaceLabel="Replace with"
              stripHelper="Spaces are left as-is."
              replaceHelper="Each space becomes this."
              inputPlaceholder="e.g. _ or ."
            />
          </Field>
          <Field label="Remove characters" helper="Deleted from the name.">
            <TextInput
              value={options.removeCharacters}
              onChange={(v) => {
                set("removeCharacters", v);
              }}
              placeholder="e.g. ,#"
            />
          </Field>
          <Field label="Case">
            <Select
              value={options.case}
              onChange={(v) => {
                set("case", v);
              }}
              options={CASE_OPTIONS}
            />
          </Field>
        </div>
        <Toggle
          label="Convert accents to plain ASCII"
          checked={options.asciiTransliterate}
          onChange={(v) => {
            set("asciiTransliterate", v);
          }}
        />
        <Toggle
          label="Normalize punctuation to ASCII"
          checked={options.normalizePunctuation}
          onChange={(v) => {
            set("normalizePunctuation", v);
          }}
          helper="Fold curly quotes, en/em dashes, and ellipses to plain ASCII."
        />
      </CollapsibleSection>

      <CollapsibleSection
        title="Length & collisions"
        summary="Length caps, what to drop when too long, duplicate suffix"
      >
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <Field label="Filename max length">
            <NumberInput
              value={options.filenameMax}
              min={1}
              onChange={(v) => {
                set("filenameMax", v);
              }}
            />
          </Field>
          <Field label="Full-path max length">
            <NumberInput
              value={options.fullPathMax}
              min={1}
              onChange={(v) => {
                set("fullPathMax", v);
              }}
            />
          </Field>
        </div>
        <div>
          <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-muted">
            Drop order
          </span>
          <p className="mb-2 text-xs text-secondary">
            Fields dropped (top first) when the name is too long.
          </p>
          <div className="space-y-2">
            <TagListInput
              values={options.dropOrder}
              onChange={(v) => {
                set("dropOrder", v);
              }}
              ordered
              suggestions={BARE_TOKENS}
              placeholder="Add a token — type to search"
              ariaLabel="Drop order"
            />
            <TokenAdvisory values={options.dropOrder} />
          </div>
        </div>
        <Field
          label="Duplicate suffix format"
          helper="{n} is added only when a name already exists."
        >
          <ExampleSelect
            value={options.duplicateSuffixFormat}
            onChange={(v) => {
              set("duplicateSuffixFormat", v);
            }}
            options={SUFFIX_FORMAT_OPTIONS}
            customPlaceholder=" ({n})"
          />
        </Field>
      </CollapsibleSection>

      <CollapsibleSection
        title="Cross-drive concurrency"
        summary="How many transfers and renames run at once"
      >
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <Field
            label="Cross-volume concurrency"
            helper="Files copied across drives at once. 2 for hard drives; 4–8 if both are SSDs. Higher isn't always faster."
          >
            <NumberInput
              value={options.crossVolumeConcurrency}
              min={1}
              max={16}
              onChange={(v) => {
                set("crossVolumeConcurrency", v);
              }}
            />
          </Field>
          <Field
            label="Same-volume concurrency"
            helper="Same-drive renames are instant; the default is fine."
          >
            <NumberInput
              value={options.sameVolumeConcurrency}
              min={1}
              max={16}
              onChange={(v) => {
                set("sameVolumeConcurrency", v);
              }}
            />
          </Field>
        </div>
      </CollapsibleSection>

      {/* Excludes — the pre-routing skip list, sibling to routing so the two stay parallel.
            These are evaluated before any routing rule; a matching item is dropped from the batch
            entirely (neither renamed nor moved), so they are the safest way to fence off items you
            never want this extension to touch. All three flow through set() like every other control. */}
      <CollapsibleSection
        title="Excludes"
        summary="Skip items by tag, studio, or source path — before any routing"
      >
        <EntitySelectField
          entityType="tag"
          label="Exclude by tag"
          labelStyle="group"
          values={options.excludeTagIds}
          onChange={(v) => {
            set("excludeTagIds", v);
          }}
          placeholder="Search tags…"
        />

        <EntitySelectField
          entityType="studio"
          label="Exclude by studio"
          labelStyle="group"
          values={options.excludeStudioIds}
          onChange={(v) => {
            set("excludeStudioIds", v);
          }}
          placeholder="Search studios…"
        />

        <SubBlock heading="Exclude by source path">
          <ObjectArrayEditor<ExcludeRule>
            rows={options.excludePaths}
            onChange={(rows) => {
              set("excludePaths", rows);
            }}
            makeRow={() => ({ pattern: "", isRegex: false })}
            renderRow={(row, _i, update) => (
              <>
                <Field label="Source path">
                  <TextInput
                    value={row.pattern}
                    onChange={(v) => {
                      update({ pattern: v });
                    }}
                    mono
                    placeholder="Exact path or regex"
                  />
                </Field>
                <Toggle
                  label="Match as a regex"
                  checked={row.isRegex}
                  onChange={(v) => {
                    update({ isRegex: v });
                  }}
                />
                <RegexValidity pattern={row.pattern} isRegex={row.isRegex} />
              </>
            )}
            addLabel="Add exclude rule"
          />
        </SubBlock>
      </CollapsibleSection>

      {/* Field rewriting — shapes a token's value BEFORE the template renders (mirroring the
            ordering note on "Destination routing"/"Excludes"): literal per-token replaces, leading-
            article stripping, the name-shaping toggles, and the per-token whitespace map. All flow
            through set() like every other control. */}
      <CollapsibleSection
        title="Field rewriting & name shaping"
        summary="Literal token replacements, article stripping, and name shaping"
      >
        <SubBlock
          heading="Per-token replacements"
          description="Find and replace inside a single token's value."
        >
          <ObjectArrayEditor<FieldReplaceRule>
            rows={options.fieldReplacers}
            onChange={(rows) => {
              set("fieldReplacers", rows);
            }}
            makeRow={() => ({ targetToken: TOKEN_OPTIONS[0].value, find: "", replace: "" })}
            renderRow={(row, _i, update) => {
              // A rule saved before this dropdown existed (or via a hand-edited blob) may hold a
              // token outside the 18 — surface it as an extra option so the Select shows the real
              // stored value instead of silently displaying the first option while state differs.
              const tokenOptions = TOKEN_OPTIONS.some((o) => o.value === row.targetToken)
                ? TOKEN_OPTIONS
                : [
                    ...TOKEN_OPTIONS,
                    { value: row.targetToken, label: `${row.targetToken} (unknown)` },
                  ];
              return (
                <>
                  <Field label="Target token">
                    <Select
                      value={row.targetToken}
                      onChange={(v) => {
                        update({ targetToken: v });
                      }}
                      options={tokenOptions}
                    />
                  </Field>
                  <Field label="Find" helper="Literal text to match. Empty does nothing.">
                    <TextInput
                      value={row.find}
                      onChange={(v) => {
                        update({ find: v });
                      }}
                      placeholder="Text to find"
                    />
                  </Field>
                  <Field label="Replace with">
                    <TextInput
                      value={row.replace}
                      onChange={(v) => {
                        update({ replace: v });
                      }}
                      placeholder="Replacement (blank to remove)"
                    />
                  </Field>
                </>
              );
            }}
            addLabel="Add replacement"
          />
        </SubBlock>

        <div>
          <Toggle
            label="Strip a leading article from the title"
            checked={options.stripLeadingArticles}
            onChange={(v) => {
              set("stripLeadingArticles", v);
            }}
            helper="Removed once, from the start of the title."
          />
          <div className="mt-2">
            <TagListInput
              values={options.articles}
              onChange={(v) => {
                set("articles", v);
              }}
              placeholder="Add article, press Enter"
              ariaLabel="Articles"
            />
          </div>
        </div>

        <Toggle
          label="Remove spaces from studio names"
          checked={options.squeezeStudioNames}
          onChange={(v) => {
            set("squeezeStudioNames", v);
          }}
        />
        <Toggle
          label="Drop a performer already in the title"
          checked={options.preventTitlePerformer}
          onChange={(v) => {
            set("preventTitlePerformer", v);
          }}
          helper="Only when the name appears as a whole word."
        />
        <Toggle
          label="Collapse repeated folder segments"
          checked={options.preventConsecutiveSegments}
          onChange={(v) => {
            set("preventConsecutiveSegments", v);
          }}
          helper="Affects the folder path, not the filename."
        />
      </CollapsibleSection>
    </SectionCard>
  );
}
