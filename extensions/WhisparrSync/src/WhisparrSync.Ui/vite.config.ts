import react from "@vitejs/plugin-react";
import { createExtensionViteConfig } from "../../../../shared/ui-shared/vite/createExtensionViteConfig";

export default createExtensionViteConfig({ packageDir: __dirname, reactPlugin: react() });
