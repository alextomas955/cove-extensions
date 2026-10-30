import { extensionApi } from "@cove-extensions/ui-shared";

// Byte-identical to the C# manifest id: the host mounts this extension's endpoints under it.
const EXTENSION_ID = "com.alextomas955.scanandidentify";

/** Route builder bound to this extension: `api("capabilities")` → `/extensions/<id>/capabilities`. */
export const api = extensionApi(EXTENSION_ID);
