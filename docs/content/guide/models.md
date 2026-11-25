---
title: "Models and catalog"
description: "Refresh models.dev metadata and understand model capabilities."
---

# Models and catalog

CodeAssist shares one live models.dev catalog service across provider discovery, model inspection, and adapter metadata. There is no bundled catalog snapshot.

## Refresh and cache

```sh
code-assist providers models --refresh
code-assist providers models anthropic
code-assist providers presets --catalog
```

`--refresh` fetches metadata with bounded requests and ETag revalidation. A failed refresh reports a warning and keeps the last-good cache. Without `--refresh`, model listing reads the catalog already available locally.

| `catalog.refresh` | Current behavior                                     |
| ----------------- | ---------------------------------------------------- |
| `manual`          | Refresh explicitly with `providers models --refresh` |
| `never`           | Cache-only; explicit network refresh is rejected     |
| `daily`           | Reserved value; daily scheduling is not wired yet    |

A fresh checkout may have no cached models. That does not prevent inspecting built-in provider presets or defining a custom endpoint.

## Read model metadata

The model table includes provider ID, model ID, context window, maximum output, tool support, reasoning metadata, prices per million tokens, and status. Missing values are unknown, not zero or a promise of support.

Pricing is catalog information, not a billing estimate or a guarantee about your account. Verify prices and model availability with your provider before making paid requests through an SDK consumer.

```sh
code-assist providers models anthropic --json
```

## Reference a model

Model references use `provider/model`. Model IDs can themselves contain `/`; the initial segment identifies the configured provider.

```sh
code-assist config set agent.model work/gpt-4.1
code-assist config get agent.model --origin
```

The parser also recognizes `provider/model@variant`, but named-variant execution is pending. Setting a model reference does not launch a turn.

## Understand capability checks

Explicit settings override cached metadata. For known models, the adapter decorator checks declared tool and media capabilities, omits unsupported temperature, and clamps output limits for normal and streaming calls. It does not modify caller-owned options.

Unknown capability metadata stays unknown. A catalog entry is neither proof of working credentials nor a live account test. See [providers](/guide/providers/) for routing and [troubleshooting](/reference/troubleshooting/) for refresh failures.
