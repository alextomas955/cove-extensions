import { themes as prismThemes } from "prism-react-renderer";
import type { Config } from "@docusaurus/types";
import type * as Preset from "@docusaurus/preset-classic";

const config: Config = {
  title: "Cove Extensions",
  tagline: "Community extensions for Cove by alextomas955. Not an official Cove project.",
  favicon: "img/favicon.svg",

  future: {
    v4: true,
  },

  // The GitHub Pages project-subpath split.
  url: "https://alextomas955.github.io", // the domain only, never the subpath
  baseUrl: "/cove-extensions/", // the subpath, with a leading and a trailing slash

  organizationName: "alextomas955",
  projectName: "cove-extensions",
  trailingSlash: false, // set explicitly - do not leave undefined

  onBrokenLinks: "throw",

  i18n: {
    defaultLocale: "en",
    locales: ["en"],
  },

  // Parse `.md` as CommonMark and reserve MDX for `.mdx`. The Docusaurus default parses every `.md`
  // as MDX, so an HTML comment or a bare `<Word>` anywhere in a sourced file fails the build. This
  // site sources each extension's own `docs/` folder, including a `CHANGELOG.md` whose primary reader
  // is GitHub, where an HTML comment is the only way to hide a note.
  markdown: {
    format: "detect",
  },

  presets: [
    [
      "classic",
      {
        docs: {
          routeBasePath: "/", // the docs plugin owns the site root
          sidebarPath: "./sidebars.ts",
        },
        blog: false,
        theme: {
          customCss: "./src/css/custom.css",
        },
      } satisfies Preset.Options,
    ],
  ],

  // Each extension owns its docs under extensions/<Name>/docs, and one plugin-content-docs instance
  // per extension sources that folder, so there is no site copy to drift from. The preset above keeps
  // the default instance at routeBasePath '/' with no custom id: giving every docs instance a custom
  // id trips docusaurus#211. routeBasePath prefixes stay distinct across instances.
  plugins: [
    [
      "@docusaurus/plugin-content-docs",
      {
        id: "renamer",
        path: "../extensions/Renamer/docs",
        routeBasePath: "/extensions/renamer",
        sidebarPath: "./sidebars-renamer.ts",
      },
    ],
    [
      "@docusaurus/plugin-content-docs",
      {
        id: "scanandidentify",
        path: "../extensions/ScanAndIdentify/docs",
        routeBasePath: "/extensions/scan-and-identify",
        sidebarPath: "./sidebars-scanandidentify.ts",
      },
    ],
    [
      "@docusaurus/plugin-client-redirects",
      {
        // Pages that were renamed or split, so a link someone saved still lands somewhere useful.
        redirects: [{ from: "/extensions/renamer/guide", to: "/extensions/renamer/quick-start" }],
      },
    ],
  ],

  // Offline local search: no Algolia and no network at query time. Registered as a theme, so the
  // classic theme renders its built-in navbar search box.
  themes: [
    [
      "@easyops-cn/docusaurus-search-local",
      {
        hashed: true,
        indexDocs: true,
        // The blog plugin is disabled above, so indexing it would only warn about a missing blog/ dir.
        indexBlog: false,
        // One entry per docs instance. docsRouteBasePath (where pages route) and docsDir (where the
        // markdown lives) must be parallel arrays, or the indexer won't read the external instances.
        docsRouteBasePath: ["/", "/extensions/renamer", "/extensions/scan-and-identify"],
        docsDir: ["docs", "../extensions/Renamer/docs", "../extensions/ScanAndIdentify/docs"],
      },
    ],
  ],

  themeConfig: {
    colorMode: {
      respectPrefersColorScheme: true,
    },
    navbar: {
      title: "Cove Extensions",
      logo: { alt: "", src: "img/logo.svg" },
      items: [
        {
          type: "dropdown",
          label: "Extensions",
          position: "left",
          items: [
            {
              type: "docSidebar",
              docsPluginId: "renamer",
              sidebarId: "renamerSidebar",
              label: "Renamer",
            },
            {
              type: "docSidebar",
              docsPluginId: "scanandidentify",
              sidebarId: "scanAndIdentifySidebar",
              label: "Scan and Identify",
            },
          ],
        },
        {
          type: "docSidebar",
          sidebarId: "docsSidebar",
          label: "Contributing",
          position: "left",
        },
        {
          href: "https://github.com/alextomas955/cove-extensions",
          label: "GitHub",
          position: "right",
        },
      ],
    },
    footer: {
      style: "dark",
      links: [
        {
          title: "Extensions",
          items: [
            { label: "All extensions", to: "/#extensions" },
            { label: "Renamer", to: "/extensions/renamer" },
            { label: "Scan and Identify", to: "/extensions/scan-and-identify" },
          ],
        },
        {
          title: "Project",
          items: [
            { label: "Contributing", to: "/contributing" },
            {
              label: "Security",
              href: "https://github.com/alextomas955/cove-extensions/blob/main/SECURITY.md",
            },
            {
              label: "Code of Conduct",
              href: "https://github.com/alextomas955/cove-extensions/blob/main/CODE_OF_CONDUCT.md",
            },
          ],
        },
        {
          title: "Cove",
          items: [
            { label: "Cove documentation", href: "https://yourcove.net/docs/" },
            { label: "Cove on GitHub", href: "https://github.com/yourcove/cove" },
          ],
        },
      ],
      copyright: `Community extensions by alextomas955, not affiliated with or endorsed by the Cove project. Copyright © ${new Date().getFullYear()} alextomas955.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
    },
  } satisfies Preset.ThemeConfig,
};

export default config;
