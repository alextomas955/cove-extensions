# Website

The docs site for the `cove-extensions` monorepo, built with [Docusaurus](https://docusaurus.io/).

## Install

```bash
npm install
```

## Local development

```bash
npm start
```

Starts a local dev server and opens a browser window. Most changes are reflected live without a restart.

## Build

```bash
npm run build
```

Generates static content into the `build` directory, which can be served by any static host.

## Deployment

The site deploys to GitHub Pages via CI. To deploy manually:

```bash
GIT_USER=<your GitHub username> npm run deploy
```
