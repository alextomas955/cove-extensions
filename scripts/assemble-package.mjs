// The package assembler's command-line entry point. The whole body is the call, deliberately: this
// file is only ever reached by being run, so there is nothing to decide and therefore no decision to
// get wrong. The guard that used to stand here compared this module's URL against process.argv[1] and
// answered "no" whenever the script was reached through a junction or a symlink — Node realpaths the
// entry and that comparison did not — so the process printed nothing and exited 0, which all three
// callers read as "the declared package is on disk". The logic lives in ./assemble-package-core.mjs.
import process from "node:process";

import { main } from "./assemble-package-core.mjs";

process.exit(main(process.argv.slice(2)));
