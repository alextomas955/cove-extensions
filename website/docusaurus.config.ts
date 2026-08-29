import { themes as prismThemes } from "prism-react-renderer";
import type { Config } from "@docusaurus/types";
import type * as Preset from "@docusaurus/preset-classic";

// This runs in Node.js - Don't use client-side code here (browser APIs, JSX...)

const config: Config = {
  title: "alextomas955 Cove Extensions",
  tagline: "Community extensions for Cove by alextomas955 — not an official Cove project",
  favicon: "img/favicon.ico",

  // Future flags, see https://docusaurus.io/docs/api/docusaurus-config#future
  future: {
    v4: true, // Improve compatibility with the upcoming Docusaurus v4
  },

  // GH-Pages project-subpath values — locked verbatim from CONTENT-STRATEGY.md / CONTEXT.md.
  url: "https://alextomas955.github.io", // domain ONLY — never put the subpath here
  baseUrl: "/cove-extensions/", // subpath, leading AND trailing slash

  // GitHub pages deployment config.
  organizationName: "alextomas955",
  projectName: "cove-extensions",
  trailingSlash: false, // set explicitly — do not leave undefined

  onBrokenLinks: "throw", // scaffold default — keep it; catches dead links in the stub tree

  // Even if you don't use internationalization, you can use this field to set
  // useful metadata like html lang. For example, if your site is Chinese, you
  // may want to replace "en" with "zh-Hans".
  i18n: {
    defaultLocale: "en",
    locales: ["en"],
  },

  // Parse `.md` as CommonMark and reserve MDX for `.mdx`. Docusaurus 3 defaults to `mdx`, which
  // parses EVERY `.md` as MDX — so an HTML comment or a bare `<Word>` anywhere in a sourced file
  // fails the build. That is a live hazard here rather than a hypothetical one: this site sources
  // each extension's own `docs/` folder, and one of those pages imports the extension's
  // `CHANGELOG.md` — a file whose primary reader is GitHub, where `{/* */}` would render as
  // literal text and an HTML comment is the only correct way to hide a note. Under `detect` the
  // changelog stays valid CommonMark for GitHub and still builds here.
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
        blog: false, // no blog plugin
        theme: {
          customCss: "./src/css/custom.css",
        },
      } satisfies Preset.Options,
    ],
  ],

  // Each extension owns its docs under extensions/<Name>/docs; one plugin-content-docs
  // instance per extension sources that folder so there is a single doc source (no site copy to
  // drift from). The preset above keeps the DEFAULT instance id at routeBasePath '/' — giving only
  // these EXTRA instances custom ids is what avoids docusaurus#211 (which trips when EVERY docs
  // instance carries a custom id). routeBasePath prefixes stay distinct across instances.
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
        id: "whisparr-sync",
        path: "../extensions/WhisparrSync/docs",
        routeBasePath: "/extensions/whisparr-sync",
        sidebarPath: "./sidebars-whisparrsync.ts",
      },
    ],
  ],

  // Offline local search (no Algolia, no network at query time). Registered as a theme;
  // the classic theme then renders its built-in navbar search box automatically. Audited OK:
  // @easyops-cn org, MIT, Docusaurus 3.x-compatible. Stock styling.
  themes: [
    [
      "@easyops-cn/docusaurus-search-local",
      {
        hashed: true,
        indexDocs: true,
        // The blog plugin is disabled above, so indexing it would only warn about a missing blog/ dir.
        indexBlog: false,
        // One entry per docs instance, in both arrays. The two are not paired by index: the search
        // plugin reads docsRouteBasePath to decide which built routes count as docs, and walks
        // docsDir only to hash the source markdown into the index's cache-busting query. An
        // instance's pages reach the index because its content-docs plugin instance is registered
        // above, so an omission here surfaces as a reader holding a stale index after a docs edit
        // rather than as a build failure.
        docsRouteBasePath: ["/", "/extensions/renamer", "/extensions/whisparr-sync"],
        docsDir: ["docs", "../extensions/Renamer/docs", "../extensions/WhisparrSync/docs"],
      },
    ],
  ],

  themeConfig: {
    navbar: {
      title: "alextomas955 / Cove Extensions",
      items: [
        // PAGES-02: GitHub-special files stay at repo root (never moved/duplicated into the site) —
        // reached here via canonical github.com blob links, right-aligned by default position.
        {
          href: "https://github.com/alextomas955/cove-extensions/blob/main/README.md",
          label: "README",
          position: "right",
        },
        {
          href: "https://github.com/alextomas955/cove-extensions/blob/main/CONTRIBUTING.md",
          label: "Contributing",
          position: "right",
        },
        {
          href: "https://github.com/alextomas955/cove-extensions/blob/main/SECURITY.md",
          label: "Security",
          position: "right",
        },
        {
          href: "https://github.com/alextomas955/cove-extensions/blob/main/CODE_OF_CONDUCT.md",
          label: "Code of Conduct",
          position: "right",
        },
      ],
    },
    footer: {
      style: "dark",
      copyright: `Copyright © ${new Date().getFullYear()} alextomas955.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
    },
  } satisfies Preset.ThemeConfig,
};

export default config;
