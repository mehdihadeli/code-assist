---
title: "Contributing"
description: "Place changes, verify contracts and document delivery honestly."
---

# Contributing

Use this guide to choose the project that owns your change, run validation and update the documentation.

## Choose the owning project

Read [architecture](/development/architecture/) before adding a dependency. Pure contracts and algorithms belong below platform adapters; composition and surface projects connect those pieces. Architecture tests enforce allowed references.

Add tests under the corresponding `tests/CodeAssist.<Project>/UnitTests` or `IntegrationTests` owner. Add a suite only when there is behavior for it to cover. Keep feature folders and namespaces aligned with that owner.

## Verify the change

```sh
dotnet build CodeAssist.slnx
dotnet test --solution CodeAssist.slnx --no-progress --ignore-exit-code 8
```

Live provider tests remain opt-in. The ignored MTP exit code covers all-skipped suites, not failed tests. See [testing](/development/testing/) for focused runs and delivery checks.

## Update documentation

Update the guide that owns the workflow, then the [feature status](/guide/features/) when its delivery boundary changes. A settings field or project placeholder alone does not make a runtime feature complete.

Edit Markdown (`.md`) files directly under `docs/content`. Register new pages in `docs/site/config.mjs` and the corresponding `_meta.js` navigation file. Internal links use published routes, such as `/guide/configuration/`; Next.js applies any hosting base path. Nextra compiles these files directly, so there is no content generation step.

```sh
npm --prefix docs ci
npm --prefix docs test
npm --prefix docs run build
```

## Write for readers

Guides and READMEs should explain how to install, configure and use CodeAssist, including current limitations. Keep source-review notes, internal comparisons, task history and implementation plans out of published documentation. Put contributor-specific contracts and validation instructions in the development guides.
