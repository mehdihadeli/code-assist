---
title: "VS Code app"
description: "Install the native editor client and connect to the shared .NET runtime."
---

# VS Code App

CodeAssist has two product surfaces: CLI/TUI and a native VS Code extension. The editor connects to the same .NET server and provider configuration as the CLI. Microsoft Agent Framework and Microsoft.Extensions.AI run on the server; the extension does not contain model clients or tool execution.

## Build and Install

Requires VS Code 1.105 or later, Node.js 22 or later for development, and the .NET 10 SDK to build the runtime. From the repository root:

```sh
dotnet build CodeAssist.slnx
cd vscode-extension
npm ci
npm test
npm run package
```

Install the generated package under `artifacts/` with **Extensions: Install from VSIX**. For development, press F5 with **CodeAssist VS Code Extension** selected. The VSIX bundles the three TypeScript workspace packages, not a service binary. Configure the built server below, or provision a compatible `CodeAssist.Server` executable on the workspace host. See [providers](/guide/providers/) to configure a model.

## Connect

In a trusted workspace, run **CodeAssist: Connect**. Managed mode starts an owned `CodeAssist.Server --listen 127.0.0.1:0` process with a private generated token. The editor reads a versioned readiness record and verifies authenticated health. Disconnect or extension unload stops that child. No repository file can override the launch command.

For source development without an installed tool, put these non-secret values in VS Code **user** settings, replacing the DLL path with your checkout's absolute path:

```json
{
  "codeAssist.server.mode": "managed",
  "codeAssist.server.executable": "dotnet",
  "codeAssist.server.arguments": ["F:/your-checkout/src/CodeAssist.Server/bin/Debug/net10.0/CodeAssist.Server.dll"]
}
```

For an existing service, start it as described in the [server guide](/guide/server/), set `codeAssist.server.mode` to `external` and `codeAssist.server.endpoint` to its loopback origin, then run **CodeAssist: Set External Server Token**. The token is stored in SecretStorage, not settings, logs or a URL. Disconnect never terminates an external server.

Every connection setting accepts its equivalent transient environment variable with `__` nesting, for example `codeAssist__server__mode`, `codeAssist__server__executable` and `codeAssist__server__arguments__0`. Whole argument arrays accept JSON; indexed values override the array. These do not replace runtime environment names such as `Providers__default__ApiKey` or `Host__Listen__Http__Token`.

In SSH, WSL or a container, the extension runs on the workspace host and uses that host's server executable and loopback interface. Multi-root workspaces select a root before managed startup. Browser-hosted extensions and public remote HTTP servers are not supported.

## Conversations

Use `@codeassist` in native chat or **CodeAssist: Ask** from the command palette. **Ask About Selection** sends selected text with the entered prompt; no other open document is captured. Model requests use configured provider credentials and can incur charges. Command responses use an output channel, so this path does not require the native chat UI to be available.

Native chat conversations keep independent server sessions. `/new` starts another. **CodeAssist: New Session**, **Switch Session**, and **Show Committed History** operate on command conversations or the last selected session. **Select Model** accepts a configured `provider/model@variant` for new sessions; existing native conversations retain their server model.

Use the native Stop control or the command progress Cancel action to cancel a turn. Cancellation is cooperative and cannot undo a completed local commit. The client never automatically resends a prompt after interruption; inspect committed history before retrying.

**CodeAssist: Run Plugin Prompt** lists enabled, policy-permitted plugin text guidance from the runtime. Choose a qualified contribution and enter task text to send it through the normal command conversation. No editor source is captured. Manage installations and exact-digest approval explicitly through the CLI; the editor exposes no install/enable action. The service management API remains accessible to holders of its bearer token. See [plugins](/guide/plugins/).

**CodeAssist: Use Skill** lists human-invocable local, standard and approved-plugin skills. It prompts for declared parameters and task input, expands text through the shared server, then submits it through the normal command conversation. Cancellation before submission sends no conversation request. The command captures no editor source and does not execute gather recipes or isolated routes. Source precedence, audience and managed restrictions are described in [skills](/guide/skills/).

Ordinary conversations can also let a tool-capable model select admitted skills, load their guidance and read listed text resources through the same server. This does not capture editor documents or enable workspace edits, execution recipes or scripts. Tool visibility follows the active role, disabled-tool settings and managed policy; skill content remains untrusted guidance.

## Connector Management

**CodeAssist: Manage Connectors** lists configured servers for the selected command session, confirms startup or credential removal, and offers disconnect. **CodeAssist: Inspect Connector Prompt** retrieves a prompt from an already connected server and opens untrusted JSON without submitting it for inference. These commands do not read editor source or make local MCP tools automatically callable from chat.

Admitted provider-hosted execution emits separate call/result activity to **CodeAssist Connector Activity**. That output can contain server data; it is not a local execution or approval request. Browser authorization for a managed server may require prior interactive CLI login. See [connectors](/guide/connectors/) for authorization, cache and coverage limits.

## Delivery Boundary

The app supports text streaming, selection context, session/history/model controls and authenticated managed/external connections. The server recovers committed sessions from the same workspace/storage by default; Switch Session can select retained history after reconnecting. Concurrent clients must attach to one service because its store lease is exclusive. File/tool chat references, workspace edits, approval/question controls, durable stream replay, native checkpoint controls and automatic runtime installation remain pending. CLI conversation commands expose retained conversation checkpoints without implementing filesystem undo.

Editor trust and token storage do not make a local service multi-tenant: possession of its bearer token permits access to every session in that instance. Current protection and limits are documented in the [server guide](/guide/server/#delivery-boundary).