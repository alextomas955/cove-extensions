// The wire-type generator's command-line entry point. The whole body is the call, deliberately: this
// file is only ever reached by being run, so there is nothing to decide and therefore no decision to
// get wrong. The logic lives in ./generate-wire-types-core.mjs.
import process from "node:process";

import { main } from "./generate-wire-types-core.mjs";

process.exit(await main(process.argv.slice(2)));
