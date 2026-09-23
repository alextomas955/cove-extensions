/**
 * TokenSettingsSection - the "Token settings" card: per-token formatting for $performers, $tags,
 * $date, and $duration. Each group only renders when the current templates actually use that token
 * (so an unused token never shows noise); when none is used, an empty-state offers one-click token
 * insertion. Presentational - edits flow up through set/setMulti/insertToken.
 */
import type { ReactNode } from "react";

import {
  type RenamerOptions,
  type MultiValueOptions,
  type OverflowPolicy,
  type SortOrder,
} from "./options";
import {
  Field,
  FieldGroup,
  NumberInput,
  Select,
  SectionCard,
  GroupCard,
  Badge,
  Chip,
  ExampleSelect,
  SeparatorChips,
  ChipMultiSelect,
  OrderedPickToAdd,
  type ExampleOption,
  type SeparatorOption,
  type ValueOption,
} from "@cove-extensions/ui-shared";
import { EntitySelectField } from "./EntitySelectField";
import { templateUsesToken } from "./templateLogic";
import { optionsFor } from "./selectOptions";
import { TOKENS } from "./tokens";

const OVERFLOW_OPTIONS = optionsFor<OverflowPolicy>({
  dropAll: "Drop all when over the max",
  keepFirst: "Keep the first N",
});
// Sort orders are not interchangeable between the two groups: the engine only honors id/favorite
// ordering for performers (tags fall back to name ordering), so a tag Sort that offered them would
// silently no-op. Hence two distinct lists rather than one shared constant.
const PERFORMER_SORT_OPTIONS = optionsFor<SortOrder>({
  nameAsc: "Name (A→Z)",
  none: "Keep original order",
  idAsc: "By internal id",
  favoriteFirst: "Favorites first, then name",
});
const TAG_SORT_OPTIONS: readonly { value: SortOrder; label: string }[] = [
  { value: "nameAsc", label: "Name (A→Z)" },
  { value: "none", label: "Keep original order" },
];

// The fixed performer-gender set. The value is the C# enum name the backend matches (case-insensitive);
// the label is the friendly spelling. Shared by the ignore-genders multiselect and the gender-order
// ranking, so both offer exactly the genders the engine understands rather than free text.
const GENDER_OPTIONS: readonly ValueOption[] = [
  { value: "Male", label: "Male" },
  { value: "Female", label: "Female" },
  { value: "TransgenderMale", label: "Transgender male" },
  { value: "TransgenderFemale", label: "Transgender female" },
  { value: "Intersex", label: "Intersex" },
  { value: "NonBinary", label: "Non-binary" },
];

// Common DateFormat options; the example column uses the reference date 2026-03-12.
export const DATE_FORMAT_OPTIONS: readonly ExampleOption[] = [
  { value: "yyyy-MM-dd", example: "2026-03-12" },
  { value: "yyyy", example: "2026" },
  { value: "MM-dd-yyyy", example: "03-12-2026" },
  { value: "dd.MM.yyyy", example: "12.03.2026" },
  { value: "yyyy.MM.dd", example: "2026.03.12" },
];

// Common DurationFormat options; the example column uses the reference duration 1h 23m 45s.
// Values carry the engine's literal backslash escapes exactly (TS "hh\\-mm\\-ss" = literal hh\-mm\-ss).
// The engine renders a duration through TimeSpan.ToString, where `mm` is the minutes component
// rather than the total minutes.
export const DURATION_FORMAT_OPTIONS: readonly ExampleOption[] = [
  { value: String.raw`hh\-mm\-ss`, example: "01-23-45" },
  { value: String.raw`hh\.mm\.ss`, example: "01.23.45" },
  { value: String.raw`mm\-ss`, example: "23-45" },
];

// Common separators; each label makes the literal whitespace visible.
const SEPARATOR_OPTIONS: readonly SeparatorOption[] = [
  { value: ", ", label: "Comma + space ( , )" },
  { value: " · ", label: "Middot ( · )" },
  { value: " ", label: "Space ( ␣ )" },
  { value: " - ", label: "Dash ( - )" },
];

export interface TokenSettingsSectionProps {
  options: RenamerOptions;
  set: <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => void;
  setMulti: (group: "performers" | "tags", patch: Partial<MultiValueOptions>) => void;
  insertToken: (token: string) => void;
}

export function TokenSettingsSection({
  options,
  set,
  setMulti,
  insertToken,
}: TokenSettingsSectionProps) {
  const uses = (token: string) =>
    templateUsesToken(token, options.filenameTemplate, options.folderTemplate);
  const usesPerformers = uses("performers");
  const usesTags = uses("tags");
  const usesDate = uses("date");
  const usesDuration = uses("duration");

  return (
    <SectionCard title="Token settings" description="Formatting for individual tokens.">
      {usesPerformers ? (
        <MultiValueGroup
          title="Performers"
          group="performers"
          entityType="performer"
          options={options.performers}
          setMulti={setMulti}
          sortOptions={PERFORMER_SORT_OPTIONS}
          sortHelper="The id and favorite orders apply to performers only."
          ignoreGenders={
            <FieldGroup
              label="Ignore genders"
              helper="Removed before the max-count cap. Performers with no gender are always kept."
            >
              <ChipMultiSelect
                options={GENDER_OPTIONS}
                values={options.performers.ignoreGenders}
                onChange={(v) => {
                  setMulti("performers", { ignoreGenders: v });
                }}
              />
            </FieldGroup>
          }
          genderOrder={
            <FieldGroup label="Gender order" helper="Most-preferred first. Anyone else sorts last.">
              <OrderedPickToAdd
                options={GENDER_OPTIONS}
                values={options.performers.genderOrder}
                onChange={(v) => {
                  setMulti("performers", { genderOrder: v });
                }}
                addPrompt="Add a gender…"
                ariaLabel="Gender order"
              />
            </FieldGroup>
          }
        />
      ) : null}

      {usesTags ? (
        <MultiValueGroup
          title="Tags"
          group="tags"
          entityType="tag"
          options={options.tags}
          setMulti={setMulti}
          sortOptions={TAG_SORT_OPTIONS}
        />
      ) : null}

      {usesDate || usesDuration ? (
        <GroupCard
          badge={
            <Badge mono>
              {usesDate && usesDuration ? "$date · $duration" : usesDate ? "$date" : "$duration"}
            </Badge>
          }
          title={
            usesDate && usesDuration
              ? "Date & duration format"
              : usesDate
                ? "Date format"
                : "Duration format"
          }
        >
          {usesDate ? (
            <FieldGroup label="Date format" helper="e.g. yyyy-MM-dd">
              <ExampleSelect
                value={options.dateFormat}
                onChange={(v) => {
                  set("dateFormat", v);
                }}
                options={DATE_FORMAT_OPTIONS}
                customPlaceholder="yyyy-MM-dd"
                ariaLabel="Date format"
              />
            </FieldGroup>
          ) : null}
          {usesDuration ? (
            <FieldGroup label="Duration format">
              <ExampleSelect
                value={options.durationFormat}
                onChange={(v) => {
                  set("durationFormat", v);
                }}
                options={DURATION_FORMAT_OPTIONS}
                customPlaceholder="hh\-mm\-ss"
                ariaLabel="Duration format"
              />
            </FieldGroup>
          ) : null}
        </GroupCard>
      ) : null}

      {!usesPerformers && !usesTags && !usesDate && !usesDuration ? (
        <div className="rounded-xl border border-border bg-card p-6 text-center">
          <h3 className="text-base font-semibold text-foreground">
            No token-specific settings needed
          </h3>
          <p className="mx-auto mb-4 mt-1 max-w-md text-sm text-secondary">
            Your templates don&apos;t use $performers, $tags, $date or $duration. Add one and its
            options show up here.
          </p>
          <div className="flex flex-wrap justify-center gap-1">
            {EMPTY_STATE_TOKENS.map((t) => (
              <Chip
                key={t.token}
                selected={false}
                mono
                onClick={() => {
                  insertToken(t.insert);
                }}
              >
                {t.token}
              </Chip>
            ))}
          </div>
        </div>
      ) : null}
    </SectionCard>
  );
}

const EMPTY_STATE_TOKENS = ["$performers", "$tags", "$date", "$duration"].flatMap((name) =>
  TOKENS.filter((t) => t.token === name),
);

/** The $performers or $tags group: separator, count cap, sort, and the include and exclude lists. */
function MultiValueGroup({
  title,
  group,
  entityType,
  options,
  setMulti,
  sortOptions,
  sortHelper,
  ignoreGenders,
  genderOrder,
}: {
  title: string;
  group: "performers" | "tags";
  entityType: "performer" | "tag";
  options: MultiValueOptions;
  setMulti: TokenSettingsSectionProps["setMulti"];
  sortOptions: readonly { value: SortOrder; label: string }[];
  sortHelper?: string;
  ignoreGenders?: ReactNode;
  genderOrder?: ReactNode;
}) {
  const noun = entityType === "performer" ? "Performer" : "Tag";
  const sort = (
    <Field label="Sort" helper={sortHelper}>
      {(id) => (
        <Select
          id={id}
          value={options.sort}
          onChange={(v) => {
            setMulti(group, { sort: v });
          }}
          options={sortOptions}
        />
      )}
    </Field>
  );
  return (
    <GroupCard title={title} badge={<Badge mono>${group}</Badge>}>
      <FieldGroup label="Separator">
        <SeparatorChips
          value={options.separator}
          onChange={(v) => {
            setMulti(group, { separator: v });
          }}
          options={SEPARATOR_OPTIONS}
          customPlaceholder="Custom separator"
          ariaLabel={`${noun} separator`}
        />
      </FieldGroup>
      <div className="grid gap-4 md:grid-cols-2">
        <Field label="Max count">
          {(id) => (
            <NumberInput
              id={id}
              value={options.maxCount}
              min={0}
              placeholder="No limit"
              blankWhenZero
              onChange={(v) => {
                setMulti(group, { maxCount: v });
              }}
            />
          )}
        </Field>
        <Field label="On overflow">
          {(id) => (
            <Select
              id={id}
              value={options.onOverflow}
              onChange={(v) => {
                setMulti(group, { onOverflow: v });
              }}
              options={OVERFLOW_OPTIONS}
            />
          )}
        </Field>
      </div>
      {ignoreGenders ? (
        <div className="grid gap-4 md:grid-cols-2">
          {sort}
          {ignoreGenders}
        </div>
      ) : (
        sort
      )}
      {genderOrder}
      <EntitySelectField
        entityType={entityType}
        label="Only include"
        values={options.whitelistIds}
        onChange={(v) => {
          setMulti(group, { whitelistIds: v });
        }}
        placeholder={`Search ${group}…`}
      />
      <EntitySelectField
        entityType={entityType}
        label="Never include"
        values={options.blacklistIds}
        onChange={(v) => {
          setMulti(group, { blacklistIds: v });
        }}
        placeholder={`Search ${group}…`}
      />
    </GroupCard>
  );
}
