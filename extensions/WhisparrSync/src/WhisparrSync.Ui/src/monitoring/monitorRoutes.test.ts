/**
 * The entity verbs the rules module offers, against the entity verbs the server actually mounts.
 *
 * Both surfaces read their routes from that one module: the entity menu renders a row disabled when
 * it answers null, and the selection overlay does not offer the row at all. Whether a route exists
 * is a fact about the SERVER, and the browser cannot see it: an item wired to a route nobody mounted
 * posts into a 404, and a route mounted with no item is a verb no reader can reach. Both read as
 * nothing happening.
 *
 * The wire document is emitted from the shipped route registrations, so it is the one place that
 * knows. A source pin rather than a DOM test, and in its own file for that reason: the rendering
 * tests run under jsdom, where the filesystem is not reachable.
 *
 * The same document also declares what each of those verbs ANSWERS, which the browser has to type
 * to read anything off it, so that pairing is pinned here too.
 */
import { readFileSync } from "node:fs";
import path from "node:path";
import { test, expect } from "vitest";

import { MONITOR_ACTION_ANSWER_SCHEMAS } from "./monitorMenuLogic";

const wireDocument = path.resolve(
  import.meta.dirname,
  "..",
  "..",
  "..",
  "..",
  "wire",
  "openapi.json",
);

const rules = path.join(import.meta.dirname, "monitorMenuLogic.ts");

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

/** Each declared route constant, by the name it is declared under. */
function declaredRoutes(source: string): Map<string, string> {
  return new Map(
    [...source.matchAll(/^const ([A-Z_]+)_ROUTE = "([a-z-]+)";$/gm)].map((match) => [
      `${match[1]}_ROUTE`,
      match[2],
    ]),
  );
}

/**
 * The token each non-null entry of the secondary map is written as, verbatim.
 *
 * The entries are read rather than the whole file, so a value written as a bare string literal is a
 * token of its own here instead of disappearing into the constant scan above. That is the edit the
 * one-assertion form of this pin could be slipped past: a literal keeps the offered set correct
 * while the map stops naming the constant every other reader resolves through.
 */
function secondaryRouteTokens(source: string): string[] {
  const map = /const SECONDARY_ACTION_ROUTES[^{]*\{([\s\S]*?)^};$/m.exec(source);
  if (map === null) return [];
  return [...map[1].matchAll(/^\s*\w+:\s*(.+),$/gm)]
    .map((entry) => entry[1].trim())
    .filter((token) => token !== "null");
}

/** The component each mounted entity POST route declares for its 200 answer, by verb. */
function mountedAnswers(): Map<string, string> {
  const document = JSON.parse(readFileSync(wireDocument, "utf8")) as {
    paths: Record<
      string,
      {
        post?: {
          responses?: Record<string, { content?: Record<string, { schema?: { $ref?: string } }> }>;
        };
      }
    >;
  };

  const answers = new Map<string, string>();
  for (const [route, methods] of Object.entries(document.paths)) {
    const verb = /\/entity\/\{kind\}\/\{coveId\}\/([A-Za-z-]+)$/.exec(route)?.[1];
    const ref = methods.post?.responses?.["200"]?.content?.["application/json"]?.schema?.$ref;
    if (verb === undefined || ref === undefined) continue;
    answers.set(verb, ref.replace("#/components/schemas/", ""));
  }
  return answers;
}

test("the verbs the control can carry out are exactly the ones the server mounts", () => {
  const source = readFileSync(rules, "utf8");
  const declared = declaredRoutes(source);
  const served = secondaryRouteTokens(source);

  const offered = [
    ...new Set([
      ...declared.values(),
      ...served.map((token) => declared.get(token) ?? token.replaceAll('"', "")),
    ]),
  ];

  expect([...declared.keys()], "the rules module declares no entity verb at all").not.toHaveLength(
    0,
  );
  expect(offered.sort()).toEqual(mountedVerbs());
});

test("every verb the secondary map serves names a declared route constant", () => {
  const source = readFileSync(rules, "utf8");
  const declared = declaredRoutes(source);
  const served = secondaryRouteTokens(source);

  expect(served, "the secondary map serves no verb at all").not.toHaveLength(0);
  expect(
    served.filter((token) => !declared.has(token)),
    "a secondary verb is written as something other than a declared route constant",
  ).toEqual([]);
});

/**
 * Both sides are read, so neither is a transcribed literal.
 *
 * Goes red if a route's answer type moves on the server, if a verb is mounted with no browser
 * decision made about what it answers, or if a route is folded into a shape it does not answer.
 */
test("each acting route is typed as the answer the emitted document declares for it", () => {
  const answers = mountedAnswers();
  const declared: Record<string, string> = MONITOR_ACTION_ANSWER_SCHEMAS;

  expect([...answers.keys()].sort()).toEqual(mountedVerbs());
  expect(Object.keys(declared).sort()).toEqual(mountedVerbs());

  for (const [verb, component] of answers) {
    expect(declared[verb], verb).toBe(component);
  }
});
