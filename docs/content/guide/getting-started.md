---
title: "First steps"
description: "Build the CLI and inspect your first configuration."
---

# First steps

Start from a repository checkout. CodeAssist currently ships a buildable CLI tool project; these instructions do not assume a published NuGet release.

## Requirements

* The .NET 10 SDK selected by the repository's `global.json`.
* Network access to restore NuGet packages. Catalog refresh separately needs access to models.dev.
* Node.js 22 or later only when developing this documentation site.

Run all source commands below from the repository root.

## Build and inspect

```sh
dotnet restore CodeAssist.slnx
dotnet build CodeAssist.slnx
dotnet run --project src/CodeAssist.Cli -- --help
```

The remaining guides use the shorter `code-assist` command. Without a local tool installation, substitute `dotnet run --project src/CodeAssist.Cli --` for that prefix.

## Check effective settings

```sh
code-assist config path
code-assist config validate
code-assist config show --section agent --sources
code-assist doctor
```

`config path` reports actual settings locations. `--sources` shows the tier and file supplying each effective value. `doctor` checks local readiness without sending an inference request.

You can inspect configuration without an API key. A missing credential is a readiness issue, not a reason to commit a secret to your workspace.

## Make a reversible change

```sh
code-assist config set agent.maxTurns 12
code-assist config explain agent.maxTurns
code-assist config unset agent.maxTurns
```

The first command writes user settings; the final command removes that override. This reserved runtime value is useful here for verifying settings editing and provenance, not for launching an agent.

See [configuration](/guide/configuration/) for project scope, JSONC, managed locks, and environment overrides.

## Choose a provider

```sh
code-assist providers presets
code-assist providers list
code-assist providers models --refresh
```

Refresh contacts models.dev for metadata. It does not invoke a model. Follow [providers](/guide/providers/) to add an endpoint, then [models and catalog](/guide/models/) to inspect its metadata.

## Package locally

```sh
dotnet pack src/CodeAssist.Cli -c Release -o artifacts/packages
dotnet tool install --tool-path artifacts/tool-smoke --add-source artifacts/packages code-assist
```

The packaged command is available in the chosen tool directory. Add that directory to your shell's `PATH` or invoke its `code-assist` executable directly. Use a fresh tool directory if an older local package is already installed.

## Start a conversation

After configuring a provider and model:

```sh
code-assist run "Explain optimistic concurrency"
code-assist chat
```

These commands send model requests and can incur charges. `chat` reads one prompt per line; `/history` shows committed messages and `/exit` ends the conversation. The default owned service stops on command exit and loses its process-local sessions. For explicit attachment and session controls, see the [CLI reference](/reference/cli/#conversations).

Text conversations do not enable workspace coding tools, approvals, durable recovery or the full-screen terminal interface. Check [feature status](/guide/features/) before planning those workflows.
