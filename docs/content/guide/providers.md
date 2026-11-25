---
title: "Providers"
description: "Provider presets, endpoint routing and environment credentials."
---

# Providers

A provider entry names an endpoint and its adapter settings. Its ID is the first part of a model reference, such as `work/gpt-4.1`.

## Inspect before adding

```sh
code-assist providers list
code-assist providers presets
code-assist providers presets --catalog
```

Built-in presets are available without downloading a catalog. `--catalog` includes metadata from the shared catalog cache; [refresh it](/guide/models/#refresh-and-cache) if it is empty.

## Add a provider

```sh
code-assist providers add work --preset openai --default-model gpt-4.1
code-assist providers show work
```

This writes user settings. It does not obtain credentials, make an inference request, or start a conversation. A known catalog provider ID can also be added directly after refreshing the catalog.

Keep secrets in your process environment. For the `work` entry:

```powershell
$env:Providers__work__ApiKey = '<your-provider-key>'
```

```sh
export Providers__work__ApiKey='<your-provider-key>'
```

Replace the placeholder locally. Do not paste a real key into source, shared shell examples, settings files, or command arguments. Environment overrides do not persist into saved settings.

Alternatively, store `"apiKey": "{env:DEEPSEEK_API_KEY}"` directly beside the provider's `endpoint` and `models`, then supply `DEEPSEEK_API_KEY` through the process environment. The live-test harness also reads an ignored local `.env`, where `Providers__work__ApiKey={env:DEEPSEEK_API_KEY}` uses the same syntax. The normal CLI does not load that test-only dotenv file automatically. References remain unresolved in saved settings, fail when their variable is absent, and cannot be supplied by project settings. `config set providers.work.apiKey '{env:DEEPSEEK_API_KEY}'` saves only the reference; literal keys remain rejected.

## Route requests deliberately

| Setting     | Meaning                                                                  |
| ----------- | ------------------------------------------------------------------------ |
| `protocol`  | `auto`, `openai`, `anthropic`, `azure`, `gemini`, `vertex`, or `bedrock` |
| `endpoint`  | Explicit API endpoint; otherwise inherit adapter/catalog routing         |
| `api`       | OpenAI/Azure API selection: `auto`, `chat`, or `responses`               |
| `apiKey`    | Exact `{env:VARIABLE_NAME}` reference in trusted settings, or direct environment override |
| `canonical` | Catalog identity for metadata, without changing request routing          |

Explicit protocol and endpoint settings win. New entries default to `auto`; the built-in `default` entry explicitly uses OpenAI. Auto routing uses adapter and catalog metadata, including per-model routes.

Provider execution uses .NET SDK adapters only. Catalog `protocol` values describe wire APIs (`chat`, `responses`, `anthropic`, `azure`, `gemini`, `vertex`, or `bedrock`), never JavaScript packages. Built-in provider IDs use their registered adapters. A catalog-only provider without a supported protocol requires explicit `providers.<id>.protocol` configuration; its endpoint and credential-variable metadata alone do not identify an API. Equivalent environment overrides use `Providers__<id>__Protocol`.

Remote endpoints require HTTPS. Loopback can use HTTP without a key. Endpoint URLs with embedded credentials are rejected. Azure, Vertex, and Bedrock adapters support their native cloud identity setup; a generic API key is not a substitute for that provider's SDK identity configuration.

## Custom compatible endpoints

```sh
code-assist providers add local --protocol openai --endpoint http://127.0.0.1:8080/v1/
code-assist providers show local
```

This defines an OpenAI-compatible loopback endpoint. It does not start a local model server or discover its models. Unknown catalog metadata remains unknown rather than rejecting every custom model.

## Remove an entry

```sh
code-assist providers remove work
```

Removal changes settings, not your provider account or shell environment. Remove an environment variable separately when you no longer need it.

## Current limits

Provider adapters and capability checks exist as SDK components. Interactive provider login, a credential vault, OAuth, automatic local discovery, and the agent request pipeline are pending. Read [feature status](/guide/features/) for the runtime boundary.
