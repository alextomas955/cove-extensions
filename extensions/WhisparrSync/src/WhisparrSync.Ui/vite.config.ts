import react from "@vitejs/plugin-react";
import { createExtensionViteConfig } from "../../../../shared/cove-extensions-ui/vite/createExtensionViteConfig";

export default createExtensionViteConfig({ packageDir: __dirname, reactPlugin: react() });
