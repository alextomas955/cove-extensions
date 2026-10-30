import { extensionApi } from "@cove-extensions/ui-shared";

/** The endpoint prefix, byte-identical to the C# manifest id. */
export const EXTENSION_ID = "com.alextomas955.scanandidentify";

/** Route builder bound to this extension: `api("capabilities")` → `/extensions/<id>/capabilities`. */
export const api = extensionApi(EXTENSION_ID);
