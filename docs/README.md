# Documentation site

This directory contains the CodeAssist documentation website. To use CodeAssist, start with the [getting started guide](content/guide/getting-started.md). The instructions below cover editing, previewing and publishing the site.

The site uses Nextra 4 and Next.js static export. Edit Markdown (`.md`) in `content/`; Nextra compiles these files directly, including embedded components and Mermaid diagrams.

## Content ownership

- `content/index.md`: product overview and captured CLI examples.
- `content/guide/`: setup, configuration, providers, catalog, connectors, tools, and feature status.
- `content/reference/`: CLI reference and troubleshooting.
- `content/development/`: architecture, testing, and contributing.
- `site/config.mjs`: sidebar groups, search fallback, and previous/next ordering.
- `content/**/_meta.js`: Nextra navigation metadata.

Edit `.md` files directly. Register new pages in the page catalog and its corresponding `_meta.js`. Internal links use published routes such as `/guide/providers/`, not source-file paths. Content tests reject unpublished destinations, stale pages and `.mdx` content files.

## Local development

Use Node.js 22 or later:

```sh
cd docs
npm ci
npm run dev
```

Markdown, component, and style edits reload directly. Starting development or a build never rewrites `content/`.

To validate and preview the complete static site, including search:

```sh
npm test
npm run build
npm start -- --listen 3100
```

The build type-checks the app, exports to `out/`, and indexes all documentation pages with Pagefind. Search uses that production index, with title/description/group matching as a fallback during development. Use the static preview to test full-text search. `npm run typecheck` is available separately after the first build.

## Hosting

The docs workflow tests and builds pull requests, and deploys successful `main` builds to GitHub Pages. Enable GitHub Actions as the repository's Pages source before the first deployment. No GitHub Pages settings are changed by local builds.

Project builds on GitHub Actions use the repository name as the base path. User/organization sites ending in `.github.io` use `/`. Set `DOCS_BASE_PATH` explicitly for another hosting path, or to an empty string for a custom domain at the root. Internal links stay root-relative; Next.js Link applies the configured base path once. Pagefind assets use that same build-time prefix.

## Dependencies and assets

The lockfile pins the tested dependency tree. XML and math renderer overrides select patched releases. The custom shell lives in `components/`, with shared styling in `app/globals.css`. Nextra still requires `mdx-components.tsx` for element and component mappings, even when authored pages use `.md`.

The current Nextra dependency chain still reports an unpatched `braces` glob-expansion denial-of-service advisory. It affects build tooling; build only trusted repository content and recheck `npm audit` when updating Nextra. Do not use `npm audit fix --force` to downgrade the site across framework major versions.

Geist and IBM Plex Mono fonts are bundled locally, so builds and readers do not need Google Fonts. Interface icons use Lucide. CLI previews show captured output, not commands executing inside the browser.
