import { describe, expect, it } from "vitest";

import { ACTION_REFRESH, SYNC_COUNT, SYNC_IS_COUNTING } from "../common/ui/copy";
import { countControl, groupThousands } from "./syncLibraryLogic";

describe("a count reads the same wherever it is rendered", () => {
  // Hand-transcribed. An expectation computed from the module would agree with whatever grouping
  // the module produced, and the point of grouping by hand is that the rendering is fixed.
  it("groups every three digits and leaves shorter numbers alone", () => {
    expect(groupThousands(0)).toBe("0");
    expect(groupThousands(1)).toBe("1");
    expect(groupThousands(999)).toBe("999");
    expect(groupThousands(1000)).toBe("1,000");
    expect(groupThousands(1648)).toBe("1,648");
  });

  it("groups a library-scale figure", () => {
    expect(groupThousands(5898)).toBe("5,898");
    expect(groupThousands(1234567)).toBe("1,234,567");
  });
});

describe("the count control's name and its one reason", () => {
  it("is named for a first press until a result exists, and for a recount after", () => {
    expect(countControl(false, false).name).toBe(SYNC_COUNT);
    expect(countControl(false, true).name).toBe(ACTION_REFRESH);
  });

  it("cannot be pressed while a count is in flight, and says why", () => {
    expect(countControl(true, false).reason).toBe(SYNC_IS_COUNTING);
    expect(countControl(true, true).reason).toBe(SYNC_IS_COUNTING);
  });

  /**
   * A failed count is not a reason. The failure belongs in the preview region beside the control,
   * and a control dimmed after a failure would leave the reader nothing to retry with.
   */
  it("is pressable whenever no count is in flight", () => {
    expect(countControl(false, false).reason).toBeNull();
    expect(countControl(false, true).reason).toBeNull();
  });
});
