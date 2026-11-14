/**
 * Bundle entry. The `components` key `ScanAndIdentifyPage` must equal, byte for byte, the
 * componentName the C# settings section declares. A mismatch renders a host placeholder and reports
 * nothing: not in the browser console, not in the backend log.
 */
import { defineExtension } from "@cove/extension-sdk";

// Imported for its effect: it registers the interceptor that turns a failed request from the
// generated client into an ApiError carrying the status, which every error branch reads.
import "./common/lib/wireErrors";
import { ScanAndIdentifyPage } from "./settings/ScanAndIdentifyPage";

const mod = defineExtension({ components: { ScanAndIdentifyPage } });

export default mod;
