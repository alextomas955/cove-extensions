/**
 * The host symbols this surface renders through, in one module.
 *
 * Nothing checks the hand-transcribed declarations behind `@cove/runtime/components`: a wrong
 * prop shape type-checks, and a wrong export name throws at bundle load and takes every surface
 * of the extension with it. One module bounds that exposure.
 */

export {
  ConfirmDialog,
  DetailListPagination,
  PerformerTile,
  TagBadge,
  useKeySequence,
  useMultiSelect,
  toggleOptionsFromEvent,
  withOrderedToggle,
} from "@cove/runtime/components";
