import { extensionApi } from "@cove-extensions/ui-shared";

/** The endpoint prefix, byte-identical to the C# manifest id. */
export const EXTENSION_ID = "com.alextomas955.renamer";

/** Route builder bound to this extension: `api("preview")` → `/extensions/<id>/preview`. */
export const api = extensionApi(EXTENSION_ID);
