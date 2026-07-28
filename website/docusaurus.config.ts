import {themes as prismThemes} from 'prism-react-renderer';
import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

// This runs in Node.js - Don't use client-side code here (browser APIs, JSX...)

const config: Config = {
  title: 'alextomas955 Cove Extensions',
  tagline: 'Community extensions for Cove by alextomas955 — not an official Cove project',
  favicon: 'img/favicon.ico',

  // Future flags, see https://docusaurus.io/docs/api/docusaurus-config#future
  future: {
    v4: true, // Improve compatibility with the upcoming Docusaurus v4
  },

  // GH-Pages project-subpath values — locked verbatim from CONTENT-STRATEGY.md / CONTEXT.md.
  url: 'https://alextomas955.github.io', // domain ONLY — never put the subpath here
  baseUrl: '/cove-extensions/', // subpath, leading AND trailing slash

  // GitHub pages deployment config.
  organizationName: 'alextomas955',
  projectName: 'cove-extensions',
  trailingSlash: false, // set explicitly — do not leave undefined

  onBrokenLinks: 'throw', // scaffold default — keep it; catches dead links in the stub tree

  // Even if you don't use internationalization, you can use this field to set
  // useful metadata like html lang. For example, if your site is Chinese, you
  // may want to replace "en" with "zh-Hans".
  i18n: {
    defaultLocale: 'en',
    locales: ['en'],
  },

  presets: [
    [
      'classic',
      {
        docs: {
          routeBasePath: '/', // D-02: docs plugin owns the site root
          sidebarPath: './sidebars.ts',
        },
        blog: false, // D-01: remove the blog plugin entirely
        theme: {
          customCss: './src/css/custom.css',
        },
      } satisfies Preset.Options,
    ],
  ],

  // U-36: each extension owns its docs under extensions/<Name>/docs; one plugin-content-docs
  // instance per extension sources that folder so there is a single doc source (no site copy to
  // drift from). The preset above keeps the DEFAULT instance id at routeBasePath '/' — giving only
  // these EXTRA instances custom ids is what avoids docusaurus#211 (which trips when EVERY docs
  // instance carries a custom id). routeBasePath prefixes stay distinct across instances.
  plugins: [
    [
      '@docusaurus/plugin-content-docs',
      {
        id: 'renamer',
        path: '../extensions/Renamer/docs',
        routeBasePath: '/extensions/renamer',
        sidebarPath: './sidebars-renamer.ts',
      },
    ],
  ],

  // D-07: offline local search (no Algolia, no network at query time). Registered as a theme;
  // the classic theme then renders its built-in navbar search box automatically. Audited OK in
  // 16-UI-SPEC.md (@easyops-cn org, MIT, Docusaurus 3.x-compatible). Stock styling (D-08).
  themes: [
    [
      '@easyops-cn/docusaurus-search-local',
      {
        hashed: true,
        indexDocs: true,
        // The blog plugin is disabled above, so indexing it would only warn about a missing blog/ dir.
        indexBlog: false,
        // One entry per docs instance. docsRouteBasePath (where pages ROUTE) and docsDir (where the
        // markdown LIVES) must be PARALLEL arrays, or the indexer won't read the external instances.
        docsRouteBasePath: ['/', '/extensions/renamer'],
        docsDir: ['docs', '../extensions/Renamer/docs'],
      },
    ],
  ],

  themeConfig: {
    navbar: {
      title: 'alextomas955 / Cove Extensions',
      items: [
        // PAGES-02: GitHub-special files stay at repo root (never moved/duplicated into the site) —
        // reached here via canonical github.com blob links, right-aligned by default position.
        {
          href: 'https://github.com/alextomas955/cove-extensions/blob/main/README.md',
          label: 'README',
          position: 'right',
        },
        {
          href: 'https://github.com/alextomas955/cove-extensions/blob/main/CONTRIBUTING.md',
          label: 'Contributing',
          position: 'right',
        },
        {
          href: 'https://github.com/alextomas955/cove-extensions/blob/main/SECURITY.md',
          label: 'Security',
          position: 'right',
        },
        {
          href: 'https://github.com/alextomas955/cove-extensions/blob/main/CODE_OF_CONDUCT.md',
          label: 'Code of Conduct',
          position: 'right',
        },
      ],
    },
    footer: {
      style: 'dark',
      copyright: `Copyright © ${new Date().getFullYear()} alextomas955.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
    },
  } satisfies Preset.ThemeConfig,
};

export default config;
