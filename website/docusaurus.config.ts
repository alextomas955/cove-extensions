import {themes as prismThemes} from 'prism-react-renderer';
import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

// This runs in Node.js - Don't use client-side code here (browser APIs, JSX...)

const config: Config = {
  title: 'Cove Extensions',
  tagline: 'Extensions for Cove, a self-hosted media library platform',
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

  themeConfig: {
    navbar: {
      title: 'Cove Extensions',
      items: [
        // Phase 16 adds a link back to GitHub-special files (README/CONTRIBUTING/etc.) here —
        // out of scope for Phase 14; leave navbar minimal for this phase.
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
