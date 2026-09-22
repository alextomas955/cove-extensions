import { describe, expect, it } from "vitest";

import {
  ACTION_REFRESH,
  CONNECT_NOT_CONFIGURED,
  SYNC_ALREADY_RUNNING,
  SYNC_COUNT,
  SYNC_DOWNLOADS_NOTHING,
  SYNC_IS_COUNTING,
  SYNC_IS_STARTING,
  SYNC_NEEDS_A_COUNT_FIRST,
  SYNC_NOTHING_LEFT_TO_SYNC,
  SYNC_REGISTERS_THE_SCENES_YOU_OWN,
  SYNC_REGISTERS_THE_STUDIOS_YOU_OWN,
  SYNC_SITE_DOWNLOADS_NOTHING,
  SYNC_SITE_NEEDS_A_COUNT_FIRST,
  SYNC_SITE_NOTHING_LEFT_TO_SYNC,
  SYNC_SITE_SKIPPED_CANNOT_BE_REGISTERED,
  SYNC_SKIPPED_CANNOT_BE_REGISTERED,
} from "../common/ui/copy";
import {
  countControl,
  groupThousands,
  monitorToggleReason,
  syncConfirmation,
  syncDisabledReason,
  syncSentences,
  type SyncControlState,
  type SyncCounts,
} from "./syncLibraryLogic";

const SCENES = syncSentences("scenes");
const SITES = syncSentences("sites");

describe("a count reads the same wherever it is rendered", () => {
  // Hand-transcribed. An expectation computed from the module would always agree with it.
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

  // A failed count is not a reason to dim the control, or the reader has nothing to retry with.
  it("is pressable whenever no count is in flight", () => {
    expect(countControl(false, false).reason).toBeNull();
    expect(countControl(false, true).reason).toBeNull();
  });
});

const LIBRARY: SyncCounts = { notYetThere: 5894, alreadyThere: 4, skipped: 1648 };
const ONE_SCENE: SyncCounts = { notYetThere: 1, alreadyThere: 0, skipped: 0 };
const NOTHING: SyncCounts = { notYetThere: 0, alreadyThere: 0, skipped: 0 };
const FULLY_HELD: SyncCounts = { notYetThere: 0, alreadyThere: 5898, skipped: 1648 };

const PRESSABLE: SyncControlState = {
  sharedReason: null,
  noConnection: false,
  syncRunning: false,
  starting: false,
  counts: LIBRARY,
  monitorAlso: false,
  sentences: SCENES,
};

describe("the confirmation names the figures and the consequence", () => {
  // Transcribed by hand. One composed from the module's own clauses would always agree with it.
  it("names what it covers and what it skips, and that it monitors nothing", () => {
    expect(syncConfirmation(LIBRARY, false, SCENES)).toBe(
      "This offers all 5,898 scenes you own to Whisparr, and skips 1,648 that cannot be registered. " +
        "It monitors nothing. Registering a scene in Whisparr downloads nothing.",
    );
  });

  it("names what monitoring does, and what it does not do by itself", () => {
    expect(syncConfirmation(LIBRARY, true, SCENES)).toBe(
      "This offers all 5,898 scenes you own to Whisparr, and skips 1,648 that cannot be registered. " +
        "It also marks each of them monitored. Monitoring a scene downloads nothing by itself. " +
        "Registering a scene in Whisparr downloads nothing.",
    );
  });

  it("reads as one scene at one, and drops the skip clause where nothing is skipped", () => {
    expect(syncConfirmation(ONE_SCENE, false, SCENES)).toBe(
      "This offers the 1 scene you own to Whisparr. It monitors nothing. " +
        "Registering a scene in Whisparr downloads nothing.",
    );
  });

  it("agrees with a single skipped scene", () => {
    expect(syncConfirmation({ notYetThere: 4, alreadyThere: 0, skipped: 1 }, false, SCENES)).toBe(
      "This offers all 4 scenes you own to Whisparr, and skips 1 that cannot be registered. " +
        "It monitors nothing. Registering a scene in Whisparr downloads nothing.",
    );
  });

  // This clause has to hold at every size, so three assertions rather than one loop over sizes.
  it("says registering downloads nothing where nothing is offered", () => {
    expect(syncConfirmation(NOTHING, false, SCENES)).toContain(SYNC_DOWNLOADS_NOTHING);
  });

  it("says registering downloads nothing at one scene", () => {
    expect(syncConfirmation(ONE_SCENE, true, SCENES)).toContain(SYNC_DOWNLOADS_NOTHING);
  });

  it("says registering downloads nothing at library scale", () => {
    expect(syncConfirmation(LIBRARY, true, SCENES)).toContain(SYNC_DOWNLOADS_NOTHING);
  });

  it("names no figure to offer where nothing was counted", () => {
    expect(syncConfirmation(NOTHING, false, SCENES)).toBe(
      "This offers all 0 scenes you own to Whisparr. It monitors nothing. " +
        "Registering a scene in Whisparr downloads nothing.",
    );
  });
});

const STUDIOS: SyncCounts = { notYetThere: 405, alreadyThere: 7, skipped: 12 };
const ONE_STUDIO: SyncCounts = { notYetThere: 1, alreadyThere: 0, skipped: 0 };

describe("the confirmation reads in studios where the run registers studios", () => {
  // Transcribed by hand, as the scene set's own pins are.
  it("names what it covers and what it skips, and that it monitors nothing", () => {
    expect(syncConfirmation(STUDIOS, false, SITES)).toBe(
      "This offers all 412 studios in your library to Whisparr, and skips 12 that cannot be " +
        "registered. It monitors nothing. Registering a studio in Whisparr downloads nothing.",
    );
  });

  // Monitoring marks scenes, not studios. The studios are registered and the scenes on them
  // carry the flag.
  it("names the scenes monitoring reaches on those studios", () => {
    expect(syncConfirmation(STUDIOS, true, SITES)).toBe(
      "This offers all 412 studios in your library to Whisparr, and skips 12 that cannot be " +
        "registered. It also marks the scenes you own on them monitored. Monitoring a scene " +
        "downloads nothing by itself. Registering a studio in Whisparr downloads nothing.",
    );
  });

  it("reads as one studio at one, and drops the skip clause where nothing is skipped", () => {
    expect(syncConfirmation(ONE_STUDIO, false, SITES)).toBe(
      "This offers the 1 studio in your library to Whisparr. It monitors nothing. " +
        "Registering a studio in Whisparr downloads nothing.",
    );
  });

  it("names no figure to offer where nothing was counted", () => {
    expect(syncConfirmation(NOTHING, false, SITES)).toBe(
      "This offers all 0 studios in your library to Whisparr. It monitors nothing. " +
        "Registering a studio in Whisparr downloads nothing.",
    );
  });

  it("says registering downloads nothing where nothing is offered", () => {
    expect(syncConfirmation(NOTHING, false, SITES)).toContain(SYNC_SITE_DOWNLOADS_NOTHING);
  });

  it("says registering downloads nothing at one studio", () => {
    expect(syncConfirmation(ONE_STUDIO, true, SITES)).toContain(SYNC_SITE_DOWNLOADS_NOTHING);
  });

  it("says registering downloads nothing at library scale", () => {
    expect(syncConfirmation(STUDIOS, true, SITES)).toContain(SYNC_SITE_DOWNLOADS_NOTHING);
  });
});

describe("the set is chosen by what the read says the run registers", () => {
  it("states studios where the read says sites", () => {
    expect(syncSentences("sites").description).toBe(SYNC_REGISTERS_THE_STUDIOS_YOU_OWN);
    expect(syncSentences("sites").skippedRemedy).toBe(SYNC_SITE_SKIPPED_CANNOT_BE_REGISTERED);
    expect(syncSentences("sites").needsACountFirst).toBe(SYNC_SITE_NEEDS_A_COUNT_FIRST);
    expect(syncSentences("sites").nothingLeftToSync).toBe(SYNC_SITE_NOTHING_LEFT_TO_SYNC);
  });

  it("states scenes where the read says scenes", () => {
    expect(syncSentences("scenes").description).toBe(SYNC_REGISTERS_THE_SCENES_YOU_OWN);
    expect(syncSentences("scenes").skippedRemedy).toBe(SYNC_SKIPPED_CANNOT_BE_REGISTERED);
    expect(syncSentences("scenes").needsACountFirst).toBe(SYNC_NEEDS_A_COUNT_FIRST);
    expect(syncSentences("scenes").nothingLeftToSync).toBe(SYNC_NOTHING_LEFT_TO_SYNC);
  });

  // Before any read has answered there is nothing to choose on, so the scene set stands.
  it("states scenes before any read has answered", () => {
    expect(syncSentences(null)).toEqual(syncSentences("scenes"));
  });
});

describe("the sync control states one reason at a time", () => {
  it("states the page's own reason over every other one in force", () => {
    expect(
      syncDisabledReason({
        ...PRESSABLE,
        sharedReason: "Cove could not read the stored connection.",
        noConnection: true,
        syncRunning: true,
        starting: true,
        counts: null,
      }),
    ).toBe("Cove could not read the stored connection.");
  });

  it("states the missing connection over a run in flight", () => {
    expect(syncDisabledReason({ ...PRESSABLE, noConnection: true, syncRunning: true })).toBe(
      CONNECT_NOT_CONFIGURED,
    );
  });

  it("states the run in flight over the enqueue in flight", () => {
    expect(syncDisabledReason({ ...PRESSABLE, syncRunning: true, starting: true })).toBe(
      SYNC_ALREADY_RUNNING,
    );
  });

  it("states the enqueue in flight over the absent count", () => {
    expect(syncDisabledReason({ ...PRESSABLE, starting: true, counts: null })).toBe(
      SYNC_IS_STARTING,
    );
  });

  it("states the absent count over there being nothing left", () => {
    expect(syncDisabledReason({ ...PRESSABLE, counts: null })).toBe(SYNC_NEEDS_A_COUNT_FIRST);
  });

  it("is pressable with counts held and nothing in flight", () => {
    expect(syncDisabledReason(PRESSABLE)).toBeNull();
  });

  // The run marks every scene the reader owns monitored, including ones the instance already
  // holds, so the same counts leave work to do with the choice on and none with it off.
  it("has nothing left to do on a fully held library with monitoring off", () => {
    expect(syncDisabledReason({ ...PRESSABLE, counts: FULLY_HELD, monitorAlso: false })).toBe(
      SYNC_NOTHING_LEFT_TO_SYNC,
    );
  });

  it("has work to do on the same library with monitoring on", () => {
    expect(syncDisabledReason({ ...PRESSABLE, counts: FULLY_HELD, monitorAlso: true })).toBeNull();
  });

  it("states its reasons in studios where the run registers studios", () => {
    expect(syncDisabledReason({ ...PRESSABLE, sentences: SITES, counts: null })).toBe(
      SYNC_SITE_NEEDS_A_COUNT_FIRST,
    );
    expect(syncDisabledReason({ ...PRESSABLE, sentences: SITES, counts: FULLY_HELD })).toBe(
      SYNC_SITE_NOTHING_LEFT_TO_SYNC,
    );
  });
});

describe("the monitor choice states one reason at a time", () => {
  it("cannot be made while a run it would apply to is in flight", () => {
    expect(monitorToggleReason({ ...PRESSABLE, syncRunning: true })).toBe(SYNC_ALREADY_RUNNING);
    expect(monitorToggleReason({ ...PRESSABLE, syncRunning: true, starting: true })).toBe(
      SYNC_ALREADY_RUNNING,
    );
  });

  it("cannot be made while the enqueue that would read it is in flight", () => {
    expect(monitorToggleReason({ ...PRESSABLE, starting: true })).toBe(SYNC_IS_STARTING);
  });

  // Nothing else disables it: it issues no request and it is read at press time.
  it("can be made whatever else the page could not do", () => {
    expect(monitorToggleReason(PRESSABLE)).toBeNull();
    expect(
      monitorToggleReason({
        ...PRESSABLE,
        sharedReason: "Cove could not read the stored connection.",
        noConnection: true,
        counts: null,
      }),
    ).toBeNull();
  });
});
