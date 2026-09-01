// The notice below is derived from `dotnet format`'s own report rather than from a probe for a Cove
// checkout, because the tool names what it actually failed to load.

const UNLOADED_REFERENCES = /^Required references did not load for (.+?) or referenced project\./gm;

export function projectsWithUnloadedReferences(output) {
  const names = new Set();
  for (const match of output.matchAll(UNLOADED_REFERENCES)) {
    names.add(match[1]);
  }
  return [...names];
}
