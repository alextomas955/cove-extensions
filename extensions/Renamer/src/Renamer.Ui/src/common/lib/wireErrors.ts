/**
 * Registers the interceptor that turns a refused request from this extension's generated client
 * into an ApiError carrying the status, which every error branch reads.
 *
 * Imported for its effect from the bundle entry, so it is in place before any surface renders. The
 * interceptor is shared; the client it is registered on is this extension's own.
 */
import { apiErrorFromResponse } from "@cove-extensions/ui-shared/extensionRequest";

import { client } from "../../wire/client.gen";

client.interceptors.error.use(apiErrorFromResponse);
