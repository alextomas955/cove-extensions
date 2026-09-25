/**
 * The host symbols this surface renders through.
 *
 * Nothing checks the hand-transcribed declarations behind `@cove/runtime/components`: a wrong
 * prop shape type-checks, and a wrong export name throws at bundle load and takes every surface
 * of the extension with it. The containerized end-to-end run loads a built bundle to prove them.
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
