---
title: "Troubleshooting"
description: "Diagnose provenance, credentials, empty catalogs and missing commands."
---

# Troubleshooting

Start with local checks. Do not share real credentials, full shell environments, or unreviewed settings output in an issue.

```sh
code-assist doctor
code-assist config validate
code-assist config path
```

## A setting has the wrong value

```sh
code-assist config get agent.maxTurns --origin
code-assist config explain agent.maxTurns
code-assist config show --section agent --sources
```

Higher-precedence tiers can replace a saved value. Managed locks and project trust rules can also reject candidates. An environment override changes the effective snapshot without rewriting its settings file.

If both exist, `settings.jsonc` takes precedence over its sibling `settings.json`. Edit the file reported by `config path` rather than guessing which file is active.

## A project change is rejected

Project files cannot introduce providers, credentials, connector processes, or allow rules. They can tighten supported limits and add deny rules. Use trusted user or managed settings for privileged definitions. Do not weaken managed policy to make a local project file win.

## A provider is not ready

```sh
code-assist providers list
code-assist providers show work
```

Replace `work` with your configured ID. Check protocol, endpoint, and credential environment variable names. API keys in files or flags are discarded with diagnostics. Azure, Vertex, and Bedrock may need their native SDK identity setup instead.

Readiness is not a successful paid inference test. The CLI currently has no conversation command for testing a model response.

## The model catalog is empty

```sh
code-assist providers models --refresh
```

A fresh installation has no bundled snapshot. Check models.dev network access and `catalog.refresh`. `never` blocks refresh; `daily` scheduling is not wired. A failed request retains the last-good cache, if one exists. Built-in presets remain available through `providers presets`.

## A connector is enabled but no tools appear

This is the current delivery boundary. Connector commands save and inspect definitions; they do not start an MCP connection or merge its tools into an agent session. See [connectors](/guide/connectors/).

## A command is missing

```sh
code-assist --help
```

The active groups are `config`, `providers`, `tools`, `connectors`, and `doctor`. Old implementation examples for chat or sessions do not describe the current CLI. Compare with [CLI reference](/reference/cli/) and [feature status](/guide/features/).

## Report a reproducible issue

Include your CodeAssist version, OS, SDK version, command with secrets removed, relevant diagnostic messages, and the smallest redacted configuration that reproduces the issue. For catalog problems, note whether a last-good cache was available. Distinguish a local deterministic failure from an opt-in live-provider failure.
