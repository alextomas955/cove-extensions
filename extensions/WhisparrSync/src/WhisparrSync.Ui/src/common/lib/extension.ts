import { extensionApi } from "@cove-extensions/ui-shared";

/** The endpoint prefix. Must match the C# manifest id exactly. */
export const EXTENSION_ID = "com.alextomas955.whisparrsync";

/** Route builder: `api("connection/test")` gives `/extensions/<id>/connection/test`. */
export const api = extensionApi(EXTENSION_ID);
