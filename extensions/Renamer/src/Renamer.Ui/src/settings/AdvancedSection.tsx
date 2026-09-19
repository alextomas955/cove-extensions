/**
 * AdvancedSection — the "Advanced" section's panels, all collapsed by default: name cleanup
 * (illegal/space handling, case, ASCII), length & collisions, cross-drive concurrency, the
 * pre-routing excludes, and field rewriting & name shaping. Sits directly under the section header
 * rather than inside a card of its own, so the header is what names the group. Presentational —
 * every field flows up through `set`.
 */
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
  GroupCard,
  ObjectArrayEditor,
  RegexValidity,
  SegmentedReplace,
  TokenPicker,
  ExampleSelect,
  type ExampleOption,
} from "@cove-extensions/ui-shared";
import { EntitySelectField } from "./EntitySelectField";
import { BARE_TOKENS } from "./templateValidation";
import { TokenAdvisory } from "./templateAdvisories";

const CASE_OPTIONS: readonly { value: CaseTransform; label: string }[] = [
  { value: "none", label: "None" },
  { value: "lower", label: "lower case" },
  { value: "title", label: "Title Case" },
];

// The 18 canonical token names a FieldReplaceRule may target, mirroring Engine/TemplateEngine.cs
// `Tokens`. The value is the canonical spelling the backend matches (case-insensitive); offering the
// closed set keeps a rule from targeting a token the engine never resolves.
const TOKEN_OPTIONS: readonly { value: string; label: string }[] = [
  "title",
  "studio",
  "parentStudio",
  "studioCode",
  "director",
  "bitrate",
  "date",
  "year",
  "height",
  "width",
  "resolution",
  "videoCodec",
  "audioCodec",
  "frameRate",
  "duration",
  "performers",
  "tags",
  "ext",
].map((t) => ({ value: t, label: t }));

// Common duplicate-suffix patterns; {n} = collision counter, shown via example.
const SUFFIX_FORMAT_OPTIONS: readonly ExampleOption[] = [
  { value: " ({n})", example: "name (1).mp4" },
  { value: "_{n}", example: "name_1.mp4" },
  { value: " - {n}", example: "name - 1.mp4" },
];

export interface AdvancedSectionProps {
  options: RenamerOptions;
  set: <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => void;
}

export function AdvancedSection({ options, set }: AdvancedSectionProps) {
  return (
    <div className="space-y-4">
      <CollapsibleSection
        title="Clean up the name"
        summary="Illegal-character and space handling, case, ASCII"
      >
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <Field label="Illegal-char replacement">
            <SegmentedReplace
              value={options.illegalReplacement}
              onChange={(v) => {
                set("illegalReplacement", v);
              }}
              stripLabel="Strip"
              replaceLabel="Replace with"
              stripHelper="Illegal characters are removed."
              replaceHelper="Each illegal character becomes this."
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
          <Field
            label="Remove characters"
            helper="Deleted before illegal-character handling, e.g. ,#"
          >
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
          label="ASCII transliterate"
          checked={options.asciiTransliterate}
          onChange={(v) => {
            set("asciiTransliterate", v);
          }}
          helper="Convert accented characters to plain ASCII."
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
        <Field label="Drop order" helper="Fields dropped (top first) when the name is too long.">
          <TagListInput
            values={options.dropOrder}
            onChange={(v) => {
              set("dropOrder", v);
            }}
            ordered
            placeholder="Add field, press Enter"
          />
          <TokenPicker
            tokens={BARE_TOKENS}
            values={options.dropOrder}
            onAdd={(name) => {
              set(
                "dropOrder",
                options.dropOrder.includes(name) ? options.dropOrder : [...options.dropOrder, name],
              );
            }}
          />
          <TokenAdvisory values={options.dropOrder} />
        </Field>
        <Field
          label="Duplicate suffix format"
          helper="{n} = a counter added only when a name already exists, e.g. name (1).mp4."
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
        summary="Skip items by tag, studio, or source path — evaluated before any routing"
      >
        <GroupCard title="Exclude by tag">
          <EntitySelectField
            entityType="tag"
            label="Tags"
            values={options.excludeTagIds}
            onChange={(v) => {
              set("excludeTagIds", v);
            }}
            placeholder="Search tags…"
          />
        </GroupCard>

        <GroupCard title="Exclude by studio" description="A child studio counts too.">
          <EntitySelectField
            entityType="studio"
            label="Studios"
            values={options.excludeStudioIds}
            onChange={(v) => {
              set("excludeStudioIds", v);
            }}
            placeholder="Search studios…"
          />
        </GroupCard>

        <GroupCard title="Exclude by source path" description="An exact match or a regex.">
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
        </GroupCard>
      </CollapsibleSection>

      {/* Field rewriting — shapes a token's value BEFORE the template renders (mirroring the
            ordering note on "Destination routing"/"Excludes"): literal per-token replaces, leading-
            article stripping, the name-shaping toggles, and the per-token whitespace map. All flow
            through set() like every other control. */}
      <CollapsibleSection
        title="Field rewriting & name shaping"
        summary="Literal token replacements, article stripping, and name shaping"
      >
        <GroupCard
          title="Per-token replacements"
          description="A literal find/replace on one token's value, before the name is shaped."
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
        </GroupCard>

        <GroupCard title="Strip leading article">
          <Toggle
            label="Strip a leading article from the title"
            checked={options.stripLeadingArticles}
            onChange={(v) => {
              set("stripLeadingArticles", v);
            }}
            helper="Case-insensitive, and only a whole word at the start."
          />
          <Field label="Articles">
            <TagListInput
              values={options.articles}
              onChange={(v) => {
                set("articles", v);
              }}
              placeholder="Add article, press Enter"
            />
          </Field>
        </GroupCard>

        <Toggle
          label="Squeeze studio names"
          checked={options.squeezeStudioNames}
          onChange={(v) => {
            set("squeezeStudioNames", v);
          }}
          helper="So one studio renders to one stable folder name."
        />
        <Toggle
          label="Drop a performer already in the title"
          checked={options.preventTitlePerformer}
          onChange={(v) => {
            set("preventTitlePerformer", v);
          }}
          helper="Only when the whole name appears in the title."
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
    </div>
  );
}
