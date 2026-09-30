// The entity verbs the rules module offers, against the entity verbs the server mounts. Whether a
// route exists is a fact about the server that the browser cannot see: an item wired to a route
// nobody mounted posts into a 404, and a route with no item is a verb no reader can reach. Both
// read as nothing happening.
//
// The wire document is emitted from the shipped route registrations, so it is the one place that
// knows. In its own file because the rendering tests run under jsdom, where the filesystem is not
// reachable. The document also declares what each verb answers, so that pairing is pinned here.
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

// Both sides are read, so neither is a transcribed literal. Goes red if a route's answer type moves
// on the server, if a verb is mounted with no browser decision about what it answers, if a route is
// folded into a shape it does not answer, or if the rules module offers a verb nobody mounted.
test("each acting route is typed as the answer the emitted document declares for it", () => {
  const answers = mountedAnswers();
  const declared: Record<string, string> = MONITOR_ACTION_ANSWER_SCHEMAS;

  expect([...answers.keys()].sort()).toEqual(mountedVerbs());
  expect(Object.keys(declared).sort()).toEqual(mountedVerbs());

  for (const [verb, component] of answers) {
    expect(declared[verb], verb).toBe(component);
  }
});
