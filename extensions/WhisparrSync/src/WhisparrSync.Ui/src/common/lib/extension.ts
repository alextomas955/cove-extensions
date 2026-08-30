import { extensionApi } from "@cove-extensions/ui-shared";

/** This extension's id — the endpoint prefix, byte-identical to the C# manifest id. */
export const EXTENSION_ID = "com.alextomas955.whisparrsync";

/** Route builder bound to this extension: `api("connection/test")` → `/extensions/<id>/connection/test`. */
export const api = extensionApi(EXTENSION_ID);
