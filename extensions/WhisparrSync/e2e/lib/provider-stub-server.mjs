// The catalogue server the provider stub runs INSIDE its container. Started with the captured page
// beside it; serves nothing it was not given.
//
// Kept as a file rather than a string passed to `node -e`: a multi-line script sent that way exits 0
// having run nothing, and the failure then looks like the stub answering wrongly.
import { createServer } from "node:http";
import { readFileSync } from "node:fs";

const captured = JSON.parse(readFileSync(process.argv[2], "utf8"));
const PAGE = captured.response.queryScenes;
const PORT = Number(process.argv[3] ?? 80);

// Page 1 is the captured page unchanged. A later page carries the same rows rotated, under ids of
// its own.
//
// Both halves are needed and for different reasons. The ids differ because a grid that saw one twice
// would be reading a pager that repeats itself. The ORDER differs because a reader - and a spec -
// tells one page from another by what is drawn at the top of it, and rows identical but for an id
// nobody renders look like the page that never turned.
function scenesFor(page) {
  const rows = PAGE.scenes;
  const offset = (page - 1) % rows.length;
  return [...rows.slice(offset), ...rows.slice(0, offset)].map((scene) =>
    page === 1 ? scene : { ...scene, id: `p${String(page)}-${scene.id}` },
  );
}

function answer(body) {
  const query = String(body?.query ?? "");
  const input = body?.variables?.input ?? {};
  const page = Number(input.page ?? 1);

  if (query.includes("MissingCount")) {
    return { queryScenes: { count: PAGE.count } };
  }
  if (query.includes("MissingPage")) {
    return { queryScenes: { count: PAGE.count, scenes: scenesFor(page) } };
  }
  // A query this stub was given no answer for. An empty object is what the real service returns for
  // a collection it holds nothing in, and it is what keeps a caller from reading a stub gap as data.
  return {};
}

createServer((request, response) => {
  let raw = "";
  request.on("data", (chunk) => (raw += chunk));
  request.on("end", () => {
    let body;
    try {
      body = JSON.parse(raw || "{}");
    } catch {
      body = null;
    }
    const data = answer(body);
    const operation = /query (\w+)/.exec(String(body?.query ?? ""))?.[1] ?? "unreadable";
    const rows = data.queryScenes?.scenes?.length ?? 0;
    console.log(
      `ASKED ${operation} page=${String(body?.variables?.input?.page ?? "-")} -> ${String(rows)} row(s)`,
    );
    response.writeHead(200, { "content-type": "application/json" });
    response.end(JSON.stringify({ data }));
  });
}).listen(PORT, "0.0.0.0", () => console.log(`provider-stub serving on ${String(PORT)}`));
