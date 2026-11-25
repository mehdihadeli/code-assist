# CodeAssist for VS Code

Native VS Code client for the shared .NET CodeAssist runtime. Includes `@codeassist` chat, command-palette ask/selection fallback, sessions, committed history, model selection and managed/external server connections.

Requires VS Code 1.105 or later and a compatible `CodeAssist.Server` executable, or an already running loopback server. Trust the workspace before connecting. Model requests use the server's configured provider and can incur provider charges.

## Use

Run **CodeAssist: Connect**, then use `@codeassist` in native chat or **CodeAssist: Ask**. **Ask About Selection** sends only the selected text with your prompt. **New Session**, **Switch Session** and **Show Committed History** manage command conversations. Native chats keep independent sessions; `/new` starts another.

**CodeAssist: Run Plugin Prompt** selects enabled text guidance, asks for task input and sends it through the normal command conversation without reading source. Install and digest-approve packages explicitly with the CLI first. The editor exposes no installation or approval action; its service token also authorizes the separate CLI management API. The repository's plugin guide covers activation and limits.

**CodeAssist: Use Skill** selects human-invocable local, standard or approved-plugin guidance, asks for declared positional parameters and task input, and submits expanded text through the normal conversation. It captures no editor files. Human visibility and managed policy are enforced by the .NET server. Expansion runs no scripts, context-gathering tools or isolated children; executable skill routes require the authorized runtime tool path, not this text command.

Managed mode launches an owned `CodeAssist.Server` process directly on an ephemeral loopback port with a private generated token. It reads a versioned readiness record and checks authenticated health. **Disconnect** or extension unload stops only that owned child. External mode uses the user setting `codeAssist.server.mode=external`, a loopback `codeAssist.server.endpoint`, and **Set External Server Token**; tokens go to VS Code SecretStorage. External processes are never stopped by the extension.

Connection settings are user/machine-only, not workspace-overridable. Equivalent transient environment names use `__`, for example `codeAssist__server__executable` or `codeAssist__server__arguments__0`. Whole argument arrays can use JSON; more specific indices win. Provider/runtime environment names remain unchanged and have no editor wrapper prefix.

For source development, build `src/CodeAssist.Server`, set `codeAssist.server.executable` to `dotnet`, and set `codeAssist.server.arguments` to an array containing the absolute built `CodeAssist.Server.dll` path. In SSH/WSL/container workspaces, provision the server on the workspace host.

## Development

From `vscode-extension/`, run `npm ci`, `npm test` and `npm run build`. This folder owns the npm lockfile, shared TypeScript configuration and internal packages under `packages/`. Press F5 with **CodeAssist VS Code Extension** selected from the repository workspace. `npm run test:host` checks activation/commands in an isolated real VS Code profile; `VSCODE_EXECUTABLE_PATH` selects an installed editor rather than downloading a test copy.

`CODE_ASSIST_TEST_SERVER` optionally points to a built server DLL for the Node suite's real .NET connection/shutdown smoke. That smoke sends no inference request. Ordinary tests use local deterministic fixtures. Implementation lives in `@codeassist/editor`, `@codeassist/editor-views` and `@codeassist/client`; this extension bundles those workspace packages.

Shared wire contracts remain in the repository's `codeassist-contracts/` folder. Run `npm ci` there before using `npm run contracts:generate` or `npm run contracts:check` from this extension folder. The client links those shared types without moving their ownership into the extension.

Run `npm run package` here to build a local VSIX under the repository's `artifacts/`, then install using VS Code's **Extensions: Install from VSIX** command. The package is private/unpublished; no server executable or provider credentials are bundled. Automatic runtime installation remains pending.

## Delivery Boundary

The server recovers committed sessions by default when the same workspace/storage reopens; use Switch Session to select retained history. One service owns each workspace store, so concurrent CLI/editor clients must attach to that service explicitly. Cancellation is best effort. Interrupted completion may have committed; inspect history before retrying. Prompts are never resent automatically. File/tool chat references, coding edits, approvals, durable stream replay and automatic runtime installation are not delivered by this client. Native chat availability depends on the editor's chat setup; command-palette operations remain the fallback.