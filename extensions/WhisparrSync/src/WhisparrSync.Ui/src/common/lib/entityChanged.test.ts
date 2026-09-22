import { expect, test } from "vitest";

import {
  announceEntityChanged,
  isTheSameEntity,
  onEntityChanged,
  type ChangedEntity,
} from "./entityChanged";

const STUDIO_SEVEN: ChangedEntity = { kind: "studio", coveId: 7 };

test("an announcement reaches every listener and carries the entity", () => {
  const heard: ChangedEntity[] = [];
  const stop = onEntityChanged((entity) => heard.push(entity));

  announceEntityChanged(STUDIO_SEVEN);
  stop();
  announceEntityChanged({ kind: "studio", coveId: 8 });

  expect(heard).toEqual([STUDIO_SEVEN]);
});

// Two surfaces over two entities are mounted at once while the host keeps a page across a
// navigation, so an announcement that matched on the number alone would read the wrong one.
test("an entity is the same only where both the kind and the number match", () => {
  expect(isTheSameEntity(STUDIO_SEVEN, { kind: "studio", coveId: 7 })).toBe(true);
  expect(isTheSameEntity(STUDIO_SEVEN, { kind: "performer", coveId: 7 })).toBe(false);
  expect(isTheSameEntity(STUDIO_SEVEN, { kind: "studio", coveId: 8 })).toBe(false);
});
