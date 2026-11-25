---
title: "Configuration"
description: "Trust tiers, environment overrides, secrets and provenance."
---

# Configuration

Precedence, lowest to highest: built-in defaults, user preferences, user settings, project files, environment, invocation flags, managed policy. Every effective key records provenance; `config explain` shows accepted and ignored candidates.

Project files can tighten limits and add deny rules, but cannot introduce providers, credentials, connector processes or allow rules. Managed locks prevent lower-tier changes. Environment and flag overrides are never persisted.

## Files and Environment

* User settings: `~/.code-assist/settings.json`; a sibling `settings.jsonc` takes precedence and allows comments/trailing commas.
* Preferences: `~/.code-assist/preferences.json`, restricted to user-only runtime presentation settings (`appearance` and `cli`). VS Code owns its own native presentation and machine/user connection preferences; see the [editor guide](/guide/vscode/).
* Project: `.code-assist/settings.json` or `.jsonc`, discovered from repository root to the working directory.
* Managed settings: OS-specific administrator directory; inspect actual locations with `config path`.
* Bootstrap overrides: `CODE_ASSIST_HOME` and `CODE_ASSIST_MANAGED_DIR`.

Every setting supports an environment variable with `__` at every nesting level, case-insensitively, including dictionary keys and list indices:

```powershell
$env:Agent__MaxTurns = '12'
$env:Providers__default__ApiKey = '<your-provider-key>'
$env:Providers__work__TimeoutSeconds = '60'
$env:Workspace__Ignore__0 = 'dist'
```

Objects, dictionaries and lists can also be supplied as whole JSON values. Nested variables override their containing section's JSON in the same environment:

```powershell
$env:Agent = '{"maxTurns":12,"maxTokens":10000}'
$env:Providers = '{"work":{"protocol":"openai","endpoint":"https://gateway.example/v1/"}}'
$env:Providers__work__TimeoutSeconds = '60'
$env:Tools__Disabled = '["fs.write","proc.run"]'
```

List values accept JSON arrays or comma-separated values. All environment values use the same schema, validation, merge modes and trust rules as other settings tiers. Managed-only keys and managed locks are not bypassed. A schema-wide regression checks environment provenance for every non-managed property, including nested dictionary/list members.

Integration live tests load these same names from the repository-root `.env` and use `ENABLED_LIVE_TESTS` as their only opt-in switch. See [testing](/development/testing/#live-provider-checks) for JSON and dotenv examples. The normal CLI reads process environment; the integration harness's dotenv loading does not change application trust rules.

Keep literal API keys outside settings files and command arguments. Trusted user/managed settings may store an exact `"apiKey": "{env:VARIABLE_NAME}"` reference; project files, flags, literal secrets and partial interpolation are rejected. References resolve once from the runtime environment only when creating a provider connection, not when loading or saving settings. Missing/empty variables and recursive references fail safely. Settings output, diagnostics and rejected managed-lock overrides remain masked.

`Providers__<id>__ApiKey` can contain a literal process credential or the same exact reference. Normal environment overrides take precedence over user settings, but never bypass managed locks. The former provider `envs` list is removed; replace it with an explicit reference. Adapter/catalog conventional environment variables remain the fallback when no explicit credential is configured. Supported unrelated old settings keys still migrate with diagnostics.

## Providers and Catalog

```json
{
  "providers": {
    "default": {
      "protocol": "openai",
      "endpoint": "https://api.openai.com/v1/",
      "maxOutputTokens": 8192,
      "timeoutSeconds": 180
    },
    "anthropic": { "apiKey": "{env:ANTHROPIC_API_KEY}" }
  },
  "agent": { "model": "default/gpt-4.1", "permissionPreset": "ask" },
  "catalog": { "refresh": "manual" }
}
```

New provider entries default to protocol `auto` and an inherited endpoint. Explicit protocol/endpoint settings win. The built-in `default` entry explicitly retains OpenAI and its endpoint. Supported protocol values are `auto`, `openai`, `anthropic`, `azure`, `gemini`, `vertex` and `bedrock`; OpenAI/Azure API selection is `auto`, `chat` or `responses`.

An `apiKey` reference stores a variable name, never its resolved value. Variable names use portable letters, digits and underscores, beginning with a letter or underscore. There is no general string, shell or file interpolation. `canonical` borrows catalog metadata without redirecting requests. SDK adapters support native cloud identity setup for Azure, Bedrock and Vertex. Configured named variants apply to delegated and HTTP sessions; vault/OAuth login and local inventory discovery remain pending. Model references use `provider/model@variant`.

Remote endpoints require HTTPS; loopback may use HTTP without a key. Invalid endpoint credentials are rejected. Provider metadata/capabilities constrain known models, while unknown metadata permits custom models. Deterministic adapter tests do not establish live account behavior.

The catalog uses the live models.dev feed with bounded fetches, ETag revalidation and a last-good cache. Settings override cached model metadata; there is no bundled snapshot. `manual` refresh is available through `providers models --refresh`; `never` prevents network refresh. The `daily` value reserves scheduling that is not wired yet.

## Connector Definitions

User/managed settings can define stdio or HTTP connectors. For example:

```json
{
  "connectors": {
    "servers": {
      "local-tools": {
        "transport": "stdio",
        "command": "dotnet",
        "arguments": ["C:/tools/my-mcp-server/server.dll"]
      }
    }
  }
}
```

`connectors list|show|add|remove|enable|disable` manage definitions. They do not connect to servers or execute tools yet. Runtime MCP startup, approvals, resources, prompts and OAuth remain pending.

## Inspect and Edit

```sh
code-assist config show --sources
code-assist config get agent.maxTurns
code-assist config set agent.maxTurns 12
code-assist config explain agent.maxTurns
code-assist config path
code-assist config validate
code-assist config schema
code-assist doctor
```

Use `config schema` for the complete current keys/defaults. JSONC edits preserve comments and reject invalid changes; managed locks and trust restrictions apply to writes. `doctor` checks settings and local readiness without sending an inference request or printing credentials. Watcher support retains the last valid snapshot after an invalid edit.

## Reserved Runtime Settings

The model includes roles, permission presets, instructions, skills, hooks, storage and UI settings ahead of runtime delivery. Accepting/validating a setting does not mean its feature is implemented. Text-only `chat`/`run` and process-local session controls exist; general agent policy evaluation, durable sessions/rewind, global instruction injection, configured hooks and the full-screen terminal interface remain pending. An explicitly attached service retains its own startup settings. See [feature coverage](/guide/features/).
