---
title: "CLI reference"
description: "Available commands, options and source-checkout invocation."
---

# CLI reference

Use `code-assist <command> --help` for the options supported by your build. In a source checkout, replace `code-assist` with `dotnet run --project src/CodeAssist.Cli --`.

## Conversations

Run `code-assist` (or `./ca` in a source checkout) without a subcommand to open chat in the current folder. Before starting its local service, interactive chat displays the workspace path and asks for acceptance. **Leave workspace** is selected by default; choosing it exits without starting the service. Choose **Allow for this launch** only after reviewing the folder. Acceptance is not saved and does not change tool permissions.

Local chat with redirected input/output or `--json` requires explicit `--trust-workspace` acceptance. Review the folder before using this flag. Attached chat (`--endpoint`) uses the already-running service and does not show the local workspace prompt. One-shot `run`, service commands and other explicit subcommands retain their existing behavior; this prompt is a chat startup safeguard, not an operating-system sandbox.

```sh
code-assist
code-assist run "Explain optimistic concurrency"
code-assist run --stdin --json < prompt.txt
code-assist chat --model default/gpt-4.1
code-assist chat --trust-workspace --json < prompts.txt
```

`run` sends one quoted prompt, or reads one prompt from standard input with `--stdin`. These inputs are mutually exclusive and capped at 65,536 characters. Text output streams directly without interpreting model text as console markup. `--json` emits one compact JSON object per line: a `session.selected` record followed by server turn events. A missing completion event is a failure, even if some answer text was already printed. There are no automatic inference retries.

Repeat `--file <path>` to attach up to 16 explicitly selected UTF-8 text files. Each file is limited to 65,536 bytes; prompt, filenames, attachment labels and contents together remain limited to 65,536 characters. Invalid UTF-8 and NUL-containing files are rejected. Attachments are labelled untrusted context, not instructions or permission grants. Images, PDFs and other binary files are not supported.

`chat` reads one prompt or slash command per line. Blank lines are ignored; `/history` prints committed messages and `/exit` or EOF exits. `/help` lists native operations and admitted guidance. Unknown commands fail before inference; prefix a literal leading slash with another slash, such as `//example`. `--json` preserves compact event output and `session.history` records. Ctrl+C cancels the command and closes its request; providers must cooperate with cancellation. Neither conversation command executes workspace coding tools or approvals. Submitting prompts or expanded guidance can incur provider charges.

Without `--endpoint`, the command starts a bundled service with a private generated token and stops it on exit. Committed conversations recover when the same workspace and storage location reopen, unless `host.recoverConversations=false`. Global model/role/settings flags apply to that owned service through existing settings tiers. Only one service may own a workspace's conversation store; attach concurrent clients to that service with `--endpoint`.

Saved conversations whose provider is no longer configured are skipped during startup, not deleted or switched to another provider. They recover on a later launch when their provider is configured again. Current policy restrictions still apply. Local text chat starts its service immediately after workspace acceptance, without requiring model credentials. It creates a model-backed session on the first prompt or session-dependent command. `/help`, `/status`, `/models`, `/skills`, `/plugins`, `/session.list` and `/exit` work before selecting a session; `/session.new <provider/model>` selects a configured model explicitly. If initial model setup fails, chat remains open with a setup diagnostic. JSON chat and attached chat retain their initial session-selection records. Model requests require a configured model and its credentials in the process environment or supported settings tiers; the checkout launchers do not automatically load `.env` files.

### Attached Sessions

Start `serve` in a separate terminal as described below, then supply its environment-only `Host__Listen__Http__Token` in the client shell:

```sh
code-assist run "Start a review" --endpoint http://127.0.0.1:8080
code-assist sessions list --endpoint http://127.0.0.1:8080 --json
code-assist sessions show <id> --endpoint http://127.0.0.1:8080
code-assist run "Continue" --session <id> --endpoint http://127.0.0.1:8080
code-assist chat --session <id> --endpoint http://127.0.0.1:8080
code-assist run "Continue newest" --latest --endpoint http://127.0.0.1:8080
code-assist run "Try another approach" --session <id> --fork --endpoint http://127.0.0.1:8080
code-assist sessions cancel <id> --endpoint http://127.0.0.1:8080
code-assist sessions delete <id> --yes --endpoint http://127.0.0.1:8080
code-assist sessions fork <id> --endpoint http://127.0.0.1:8080
code-assist sessions export <id> --file conversation.json --endpoint http://127.0.0.1:8080
code-assist sessions import --file conversation.json --endpoint http://127.0.0.1:8080
```

Only loopback service origins without paths or credentials are accepted. Health and protocol are checked before session operations. The token grants access to all sessions and admitted management operations in that single-user service; never put it in command arguments, URLs or files.

`sessions` accepts `list`, `show`, `cancel`, `delete`, `fork`, `export` or `import`, with options before or after the action. All require `--endpoint`; `list` and `import` omit the session ID. Listing is newest-first; `--limit` accepts 1 through 10,000. Deletion requires `--yes` and an idle session. Existing sessions keep their model, so `--session` or `--latest` cannot be combined with `--model`. `--latest` selects the newest session in the attached single-workspace service, not an automatically discovered global session. Both `run` and `chat` support `--latest` and `--fork`; forking requires `--session` or `--latest`.

The attached service keeps its startup settings: `--set`, `--role`, `--permission-preset` and `--max-turns` are rejected with `--endpoint`. Select `--model` only when creating a new session.

Fork and export require an idle session. Version-1 archives contain committed visible messages and serialized framework context, with a 384 KiB transfer limit. Import assigns a new ID and checks the current provider configuration and policy; it does not restore credentials, tool grants or running jobs. Export without `--file` writes the archive JSON to stdout. File export never overwrites an existing path. Archives contain sensitive conversation and model context and are not sanitized: protect them like source code and credentials. Explicit export/import can transfer context between service instances; it is not automatic crash recovery or a durable session store.

Exiting an attached client leaves the external service running. Recovery preserves committed session IDs and framework history across service restarts under current policy. Failed or truncated streams may follow an ambiguous local commit; inspect `sessions show` before retrying. Streaming is limited to 15 minutes by the client and may end earlier under the service's configured turn timeout. Common exit codes are `0` for success, `1` for runtime/stream failure, `2` for usage/configuration errors and `130` for cancellation.

## Conversation Commands

Native operations never become model prompts:

| Command | Operation |
| --- | --- |
| `/help`, `/history`, `/exit` | Discover commands, read committed messages, leave chat |
| `/session.new [model]`, `/session.list`, `/session.use id` | Create, list or select conversations |
| `/session.fork` | Fork committed context and select the new conversation |
| `/session.rename title` | Change the title |
| `/session.archive`, `/session.restore`, `/session.info` | Block/unblock new turns or inspect metadata |
| `/session.checkpoints`, `/session.rewind revision` | List retained revisions or restore conversation history, not files |
| `/usage`, `/context` | Report token/turn counters or public history size, not estimated billing or hidden context |
| `/models`, `/skills`, `/plugins`, `/connectors`, `/status` | Inspect cached metadata, admitted guidance, connector status or service readiness |

`CodeAssist.Tui` provides shared Spectre transcript, command-table, input and data views. Text and metadata are literal, not executable markup. This remains a line-oriented interface, not a full-screen application.

Configured prompts, skills and plugin guidance occupy separate namespaces: `/prompt/change-check`, `/skill/project/change-audit` and `/plugin/team.review/inspect`. They cannot override native operations. Chat explicitly expands selected guidance, then submits it through the ordinary conversation; returned text is never dispatched recursively as another command.

Store prompt definitions in the existing `commands` map in an admitted settings file. Use `config path` to locate tiers:

```json
{
	"commands": {
		"change-check": {
			"description": "Check a specific boundary in a proposed change.",
			"template": "Inspect {{arg.target}}. Task: {{args}}. Report concrete failures and existing checks."
		}
	}
}
```

Equivalent transient overrides include `Commands__change-check__Description` and `Commands__change-check__Template`. Expansion supports `{{args}}` and `{{arg.name}}` once, with values preserved as untrusted data. File includes are rejected; there is no shell preprocessing. Input and each value are limited to 4,000 characters, with at most 16 values and 64 KiB of expanded UTF-8 text. Role overrides must match the host's fixed role. A configured model requirement cannot silently switch the active conversation; select it with `/session.new model` first.

Use structured values in chat:

```text
/prompt/change-check {"input":"Look at retries","values":{"target":"request boundary"}}
/skill/project/change-audit {"input":"Look at retries","values":{"target":"request boundary"}}
```

Skill command invocation requires all declared parameters. Definitions remain in their normal [scoped skill locations](/guide/skills/); plugin prompts remain in reviewed packages. Command settings do not install files or grant tool permissions. Restart the host after changing settings; skill metadata rescans on access.

For scripts, discovery and expansion are separate from inference and create no conversation:

```sh
code-assist commands list --json
code-assist commands expand prompt/change-check --input "Look at retries" --values '{"target":"request boundary"}' --json
code-assist commands expand skill/standard/fault-trace --input "Investigate a failing request" --json
```

Both operations accept `--endpoint` to use an existing service. Expansion returns text plus any model/role requirement; scripts choose whether to submit it.

## Configuration

| Command                     | Purpose                                                                |
| --------------------------- | ---------------------------------------------------------------------- |
| `config show`               | Effective settings; `--section`, `--changed`, `--sources`, or `--json` |
| `config get <path>`         | One setting; `--origin` reports provenance                             |
| `config set <path> <value>` | Write user settings; `--scope project` selects project scope           |
| `config unset <path>`       | Remove an override                                                     |
| `config explain <path>`     | Explain a setting and value candidates                                 |
| `config path`               | Locate settings files                                                  |
| `config validate`           | Validate settings and report diagnostics                               |
| `config schema`             | Print current settings JSON Schema                                     |
| `config edit`               | Open settings in `VISUAL` or `EDITOR`, then validate                   |

Paths use dotted segments, such as `agent.maxTurns`. Environment variables use `__`, including dictionary keys and list indices. See [configuration](/guide/configuration/).

## Providers

| Command                       | Purpose                                                                      |
| ----------------------------- | ---------------------------------------------------------------------------- |
| `providers list`              | Configured providers and readiness                                           |
| `providers show <id>`         | One provider and its models                                                  |
| `providers presets`           | Built-in presets; `--catalog` includes cached catalog entries                |
| `providers models [provider]` | Cached metadata; `--refresh` fetches the latest feed                         |
| `providers add <id>`          | Add settings; supports preset, protocol, endpoint, and default model options |
| `providers remove <id>`       | Remove a settings entry                                                      |

See [providers](/guide/providers/) and [models and catalog](/guide/models/). Refresh downloads metadata, not model weights or an inference response.

## Tools

| Command             | Purpose                                       |
| ------------------- | --------------------------------------------- |
| `tools list`        | Catalog entries narrowed by role tool filters |
| `tools show <tool>` | Description and input schema                  |

Both support `--json`. They inspect components rather than execute them. See [tools](/guide/tools/).

## Connectors

| Command                   | Purpose                           |
| ------------------------- | --------------------------------- |
| `connectors list`         | Configured definitions            |
| `connectors show <id>`    | One definition                    |
| `connectors add <id>`     | Add an HTTP or process definition |
| `connectors enable <id>`  | Enable a saved definition         |
| `connectors disable <id>` | Disable without removing          |
| `connectors remove <id>`  | Remove a definition               |

These commands do not connect to MCP servers. See [connectors](/guide/connectors/).

## Plugins

`plugins` provides `init`, `validate`, `pack`, `install`, `list`, `show`, `enable`, `disable`, `remove`, `expand`, `publish`, `registry` and `stats`. Commands support `--json`; `enable` requires the exact installed `--approve` digest, and `enable`/`disable` accept `--workspace`. Registry installs use `--id` and an optional exact `--release`; changed content installs disabled. `expand` does not call a model. See [plugins](/guide/plugins/) for package examples, registry publishing, dependency rules and limits.

## Skills

`skills list` shows human-invocable guidance; add `--agent` for model visibility. `skills expand <id> --arg <value> --input <text>` expands text without inference or execution. Repeat `--arg` for declared positional parameters; `--file` supplies explicit relevance paths for agent expansion. `skills diagnose` reports malformed definitions and source collisions. All support `--json`. See [skills](/guide/skills/) for source precedence, native metadata, resources and managed admission.

## Local Agent Server

`code-assist serve [--listen <host:port>]` starts the authenticated loopback HTTP service. Set the environment-only `Host__Listen__Http__Token` secret first. The default address chooses an available loopback port; the command prints the actual URL and stays running until interrupted.

Sessions use Microsoft Agent Framework over the existing Microsoft.Extensions.AI provider clients. See the [server guide](/guide/server/) for authentication, JSON/SSE endpoints, limits and pending coding-tool/approval integration.

### Explicit API Requests

```sh
code-assist request GET /v1/health --endpoint http://127.0.0.1:8080
code-assist request GET /openapi/v1.json --endpoint http://127.0.0.1:8080
code-assist request POST /v1/sessions --data '{}' --endpoint http://127.0.0.1:8080 --json
```

`request <METHOD> <PATH>` accepts GET, POST, PUT, PATCH, DELETE, HEAD and OPTIONS. Paths stay on the selected authenticated loopback service: `/v1/...` or `/openapi/v1.json`. JSON request bodies are capped at 384 KiB; GET and HEAD reject bodies. Responses are capped at 8 MiB with a 30-second deadline. Redirects and event streams are rejected. Use `run` or `chat` for streaming. `--json` returns status, content type and body as an envelope. Failed HTTP responses expose their status, not the response body. There is no arbitrary authorization-header override or OpenAPI operation-ID resolver. Requests can mutate state or incur provider charges according to the selected endpoint.

## Agent Client Protocol

Configure an ACP-compatible client to launch `code-assist acp`. Standard input/output carry newline-delimited JSON-RPC only. Initialization negotiates protocol version 1 without starting a service or making a model request. The bridge supports `session/new`, `session/load`, `session/resume`, `session/list`, `session/delete`, `session/prompt`, `session/cancel` and streamed `session/update` notifications. Load replays committed user/assistant messages before its response; resume does not replay them. Cancellation remains readable while a prompt is running.

Session setup requires an existing absolute `cwd`. Owned mode starts a separate bundled service per workspace, up to four workspaces per connection. Attached mode uses `acp --endpoint <origin>` and accepts only the ACP process's launch directory. The external service retains its own startup workspace; this bridge does not discover or verify that remote directory. Changing a known session's workspace is rejected. Session listing covers sessions attached through this ACP connection, supports `cwd` filtering and returns a complete bounded list rather than pagination cursors.

This is a partial ACP text integration, not full protocol conformance. Text and embedded text resources are forwarded; resource links are passed as references and never fetched automatically. Image/audio prompts, client-provided MCP definitions, extra workspace roots, system-prompt overrides, client filesystem/terminal callbacks, permission requests, modes, model switching, compaction and ACP fork/close methods are not supported. Use trusted connector settings for connector management and the ordinary session CLI for explicit fork/archive operations. Owned ACP services stop on disconnect. The server can recover committed history, but the ACP connection's attachment list is not restored automatically.

## Diagnostics and version

```sh
code-assist doctor
code-assist inspect paths
code-assist inspect roles
code-assist --help
code-assist --version
```

`doctor` checks settings and local readiness, not paid model inference. See [troubleshooting](/reference/troubleshooting/) for common failures.

`inspect paths` reports resolved user/managed/project/state locations and the executable directory. `inspect roles` reports sorted role metadata and tool filters, excluding role prompt bodies and credentials. Both emit JSON, operate locally and make no provider request.

## Not available

Full-screen terminal rendering, durable stream replay, complete ACP conformance, automatic shared-service discovery, SSH worker control, shell completion, automatic updates and workspace coding/approval workflows are not implemented. Existing configuration, provider/catalog, connector, plugin, skill and diagnostic commands remain available outside conversations. The [feature status](/guide/features/) page is the delivery ledger; design documents describe a broader target.
