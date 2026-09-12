// The catalogue server the ThePornDB stub runs INSIDE its container. Started with the captured page
// beside it; serves nothing it was not given.
//
// Kept as a file rather than a string passed to `node -e`: a multi-line script sent that way exits 0
// having run nothing, and the failure then looks like the stub answering wrongly.
import { createServer } from "node:http";
import { readFileSync } from "node:fs";

const captured = JSON.parse(readFileSync(process.argv[2], "utf8"));
const PAGE = captured.response;
const PORT = Number(process.argv[3] ?? 80);

// This provider serves its catalogue as REST rows under a paging envelope, and stamps identity under
// a GraphQL spelling on another host of the same registrable domain. Both are answered here because
// both are addresses of one provider, and a stub that answered one of them would look like the
// provider being down on the other.
function answer(path) {
  if (path.startsWith("/scenes")) {
    return PAGE;
  }
  // A route this stub was given no answer for. An empty collection under the same envelope is what
  // the provider itself answers for a catalogue holding nothing, and it is what keeps a caller from
  // reading a stub gap as data.
  return { data: [], meta: { ...PAGE.meta, total: 0, to: 0 } };
}

createServer((request, response) => {
  // Drained even where nothing is read from it: a body left unconsumed holds the socket open, and
  // the caller then reads a timeout rather than the answer below.
  request.resume();
  request.on("end", () => {
    const path = String(request.url ?? "");
    const body = answer(path.split("?")[0]);
    console.log(
      `ASKED ${request.method ?? "?"} ${path} -> ${String(body.data?.length ?? 0)} row(s)`,
    );
    response.writeHead(200, { "content-type": "application/json" });
    response.end(JSON.stringify(body));
  });
}).listen(PORT, "0.0.0.0", () => console.log(`theporndb-stub serving on ${String(PORT)}`));
