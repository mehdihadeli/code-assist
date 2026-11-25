---
title: "Overview"
description: "CodeAssist commands, capabilities and current delivery boundaries."
---

# CodeAssist

CodeAssist is a .NET coding assistant with CLI/TUI and native VS Code surfaces. The CLI manages settings, providers, tool catalogs and connectors, and offers streamed `run` and line-oriented `chat` through a local agent server. The editor provides native text chat and explicit selection context over that same runtime.

<CommandPreview />

## Start with your workspace

Build the CLI from source and inspect effective settings before adding credentials. These steps do not send an inference request.

```sh
dotnet run --project src/CodeAssist.Cli -- --help
dotnet run --project src/CodeAssist.Cli -- config validate
dotnet run --project src/CodeAssist.Cli -- doctor
```

Follow [first steps](/guide/getting-started/) for requirements and a complete local setup.

## Find your next task

| Task                                          | Guide                                          |
| --------------------------------------------- | ---------------------------------------------- |
| Understand where a setting came from          | [Configuration](/guide/configuration/)         |
| Add a model endpoint without saving a secret  | [Providers](/guide/providers/)                 |
| Inspect model capabilities and cached prices  | [Models and catalog](/guide/models/)           |
| Inspect a tool's input schema and risk        | [Tools](/guide/tools/)                         |
| Prepare external tool server definitions      | [Connectors](/guide/connectors/)               |
| Diagnose invalid settings or an empty catalog | [Troubleshooting](/reference/troubleshooting/) |
| Look up a command                             | [CLI reference](/reference/cli/)               |
| Use native editor chat and selection context   | [VS Code app](/guide/vscode/)                  |

## Delivery boundaries

Settings, provider adapters, live catalog caching, and filesystem/process/web tool components are implemented. The CLI exposes management, inspection, text conversation and attached-session control commands.

> Framework-backed HTTP text sessions, CLI text streaming and the native editor text client are implemented. The durable coding actor, approvals and full-screen TUI remain pending. Configuring a reserved setting does not enable a future runtime.

The [feature status](/guide/features/) distinguishes delivered text workflows from pending coding-agent behavior.

## Build on CodeAssist

The solution separates pure contracts, platform adapters, application composition, and user-facing surfaces. Read [architecture](/development/architecture/), run [tests](/development/testing/), or follow the [contribution workflow](/development/contributing/) before changing a project boundary.
