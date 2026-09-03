/**
 * The entity verbs the control offers, against the entity verbs the server actually mounts.
 *
 * The control renders a menu item disabled when this build serves no route for it. Whether that is
 * still true is a fact about the SERVER, and the browser cannot see it: an item wired to a route
 * nobody mounted posts into a 404, and a route mounted with no item is a verb no reader can reach.
 * Both read as nothing happening.
 *
 * The wire document is emitted from the shipped route registrations, so it is the one place that
 * knows. A source pin rather than a DOM test, and in its own file for that reason: the rendering
 * tests run under jsdom, where the filesystem is not reachable.
 */
import { readFileSync } from "node:fs";
import path from "node:path";
import { test, expect } from "vitest";

const wireDocument = path.resolve(
  import.meta.dirname,
  "..",
  "..",
  "..",
  "..",
  "wire",
  "openapi.json",
);

const control = path.join(import.meta.dirname, "EntityMonitorButton.tsx");

/** Every verb the server mounts a POST for on one entity, read from the emitted document. */
function mountedVerbs(): string[] {
  const document = JSON.parse(readFileSync(wireDocument, "utf8")) as {
    paths: Record<string, Record<string, unknown>>;
  };
  return Object.entries(document.paths)
    .filter(([, methods]) => "post" in methods)
    .map(([route]) => /\/entity\/\{kind\}\/\{coveId\}\/([A-Za-z-]+)$/.exec(route)?.[1])
    .filter((verb): verb is string => verb !== undefined)
    .sort();
}

test("the verbs the control can carry out are exactly the ones the server mounts", () => {
  const source = readFileSync(control, "utf8");
  const offered = [...source.matchAll(/^const ([A-Z_]+)_ROUTE = "([a-z-]+)";$/gm)].map(
    (match) => match[2],
  );

  expect(offered, "the control declares no entity verb at all").not.toHaveLength(0);
  expect(offered.sort()).toEqual(mountedVerbs());
});
