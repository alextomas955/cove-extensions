import { extensionApi } from "@cove-extensions/ui-shared";

/** This extension's id — the endpoint prefix, byte-identical to the C# manifest id. */
const EXTENSION_ID = "com.alextomas955.whisparrsync";

/** Route builder bound to this extension: `api("scene-detail")` → `/extensions/<id>/scene-detail`. */
export const api = extensionApi(EXTENSION_ID);
