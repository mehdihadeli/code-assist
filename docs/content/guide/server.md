---
title: "Agent server"
description: "Run authenticated local agent sessions over HTTP with streamed responses."
---

# Agent Server

`CodeAssist.Server` hosts a versioned HTTP API for text conversations, catalog discovery and CLI management operations. `code-assist serve` is a launcher for that standalone executable, not an in-process host. Microsoft Agent Framework manages agent sessions and model turns. Microsoft.Extensions.AI connects those agents to the existing official .NET provider adapters. No JavaScript model runtime is involved.

This is a local, single-user service. Committed conversations recover from bounded workspace-scoped checkpoints by default; this is not a durable coding-agent actor or event-replay service. Admitted read-only skill discovery, guidance expansion and bundle-resource access are available to tool-capable models. Workspace mutation, skill execution recipes and approval execution are not enabled through these endpoints.

The [native VS Code app](/guide/vscode/) is a client of this service, not another model runtime. Managed editor mode starts an owned server with a private generated token; external mode connects to a server started below.

The CLI's `run` and line-oriented `chat` use this same API. They launch an owned service by default or attach with `--endpoint`; `sessions` commands require explicit attachment. See the [CLI reference](/reference/cli/#conversations) for prompts, JSON-line streaming and session controls.

## Start the Server

Configure a provider and `agent.model` through the normal [settings](/guide/configuration/) and [provider](/guide/providers/) paths. Supply its credentials through environment variables or supported native cloud identity.

In PowerShell 7, generate a separate server token and start the host:

```powershell
$env:Host__Listen__Http__Token = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
code-assist serve --listen 127.0.0.1:8080
```

For source-checkout invocation, replace `code-assist` with `dotnet run --project src/CodeAssist.Cli --`. The command prints its bound URL and runs until interrupted. The default configured address is `127.0.0.1:0`, which chooses an available port. Use another port if an explicit port is occupied.

Clients can launch the server directly with `dotnet run --project src/CodeAssist.Server -- --listen 127.0.0.1:8080`. Direct startup emits one JSON readiness record containing `kind`, `protocol` and `endpoint`; diagnostics go to standard error. The CLI distribution bundles service files under `service/`; managed editor mode does not require the CLI parser.

The token must contain 32 to 512 characters. It is an environment-only secret: JSON files and command-line setting overrides cannot persist it, and settings inspection redacts it. Supply the same token to API clients through their environment. Never put it in a URL. Unlike the opt-in live-test harness, `serve` does not load a repository `.env` file.

Every HTTP operation, including health, catalog and OpenAPI, requires `Authorization: Bearer <token>`. Public binding, TLS termination, pairing, multiple users and per-client permissions are not implemented. Possession of this token grants access to all sessions and the CLI management API, including policy-admitted package and connector operations. Treat it as access to this local user's runtime, not a read-only chat credential.

## Host Settings

Non-secret user settings can contain:

```json
{
  "host": {
    "maxActiveSessions": 64,
    "maxConcurrentTurns": 4,
    "turnTimeoutSeconds": 120,
    "recoverConversations": true,
    "listen": {
      "http": {
        "address": "127.0.0.1:8080",
        "allowedOrigins": ["http://localhost:3000"]
      }
    }
  }
}
```

Equivalent environment names include `Host__MaxConcurrentTurns`, `Host__TurnTimeoutSeconds`, `Host__Listen__Http__Address` and `Host__Listen__Http__AllowedOrigins__0`. `--listen` overrides the address for this invocation only. `serve` is an explicit startup action; `host.listen.http.enabled` does not automatically start a background host, and it does not prevent explicit startup.

`Host__RecoverConversations=false` disables durable conversation storage for that host. Otherwise, storage lives under `storage.directory` (or the resolved state directory), in `conversations/<workspace-hash>`. A service holds an exclusive store lease until shutdown: run one service per workspace/store and attach concurrent clients explicitly. Automatic service discovery/reuse is not implemented.

Committed snapshots preserve IDs, model references, framework history, titles, archive flags and usage counters. Recovery reapplies current host instructions and provider/tool policy, failing closed rather than partially publishing invalid sessions. Checkpoints retain 12 revisions per conversation, capped at 8 MiB each and 256 MiB total JSON storage. Checksum validation and atomic publication detect corrupt committed snapshots; this is not encrypted storage. Treat contents as sensitive model/source context and protect the state directory.

Only loopback hosts and peers are accepted. Browser requests must have the same origin or an explicitly configured origin. Wildcards, opaque `null` origins and untrusted Host headers are rejected. An allowed preflight needs no token, but the subsequent API request does. Credentials are not sent through cookies.

## Sessions and Messages

Use the printed server address and the same server token in the client environment:

```powershell
$base = 'http://127.0.0.1:8080'
$headers = @{ Authorization = "Bearer $env:Host__Listen__Http__Token" }
$session = Invoke-RestMethod -Method Post -Uri "$base/v1/sessions" -Headers $headers -ContentType 'application/json' -Body '{}'
Invoke-RestMethod -Method Post -Uri "$base/v1/sessions/$($session.id)/messages" -Headers $headers -ContentType 'application/json' -Body '{"text":"Explain optimistic concurrency."}'
```

An empty creation body object selects the configured role's model, or `agent.model` when that role has no model. Send `{"model":"provider/model@variant"}` to choose another configured route. Model aliases, configured variants, provider credentials and known capability/output restrictions follow normal provider composition. Disabled providers/models, non-chat model types and matching restrictive `provider.use` policies cannot create a usable session. Role instructions are selected at server startup, not accepted from request bodies.

| Method | Route | Result |
| --- | --- | --- |
| GET | `/v1/health` | Readiness and protocol version, without probing a provider |
| GET / POST | `/v1/sessions` | List sessions / create one with `201` and a Location header |
| GET / DELETE | `/v1/sessions/{id}` | Session snapshot / delete an idle conversation |
| GET / POST | `/v1/sessions/{id}/details` | Read metadata/usage or update `{ "title": "Review", "archived": false }` |
| GET | `/v1/sessions/{id}/checkpoints` | Retained committed revision numbers |
| POST | `/v1/sessions/{id}/rewind` | Restore conversation history using `{ "revision": 2, "expectedRevision": 5 }`; no filesystem undo |
| GET | `/v1/sessions/{id}/archive` | Export bounded committed framework context and visible history |
| POST | `/v1/sessions/{id}/fork` | Fork an idle conversation with a fresh ID and independent context |
| POST | `/v1/sessions/import` | Import a version-1 archive under current configuration; returns `201` and a fresh Location |
| GET | `/v1/sessions/{id}/messages` | Committed user and assistant messages |
| POST | `/v1/sessions/{id}/messages` | Run one text turn and return its committed assistant message |
| POST | `/v1/sessions/{id}/messages/stream` | Run one turn with Server-Sent Events |
| POST | `/v1/sessions/{id}/cancel` | Request cooperative cancellation of the active turn |
| GET | `/v1/catalog/providers` | Published provider metadata |
| GET | `/v1/catalog/models?providerId=...` | Published text-model metadata |
| GET | `/v1/catalog/models/{providerId}/{modelId}` | One published model; model IDs may contain slashes |
| POST | `/v1/catalog/refresh` | Explicit refresh; forbidden when `catalog.refresh=never` |
| GET | `/v1/plugins/commands` | Enabled, policy-permitted text contribution metadata |
| POST | `/v1/plugins/expand` | Expand `{ "name": "team.review/inspect", "input": "task" }` without inference or tools |
| GET | `/v1/skills/commands` | Human-invocable skill metadata and declared parameter names |
| GET | `/v1/commands` | Reserved native operations plus admitted namespaced prompt/skill/plugin metadata |
| POST | `/v1/commands/expand` | Expand `{ "name": "prompt/change-check", "input": "task", "values": { "target": "service" } }` as untrusted text only |
| POST | `/v1/skills/expand` | Expand `{ "id": "project/change-audit", "input": "task", "arguments": ["service"] }` as text only |
| GET | `/v1/sessions/{id}/connectors` | Configured connector status without connecting |
| GET / POST / DELETE | `/v1/sessions/{id}/connectors/{server}/connection` | Inspect / explicitly connect / disconnect |
| DELETE | `/v1/sessions/{id}/connectors/{server}/credentials` | Forget locally owned credentials without issuer revocation |
| GET | `/v1/sessions/{id}/connectors/{server}/prompts` | Untrusted prompt metadata |
| POST | `/v1/sessions/{id}/connectors/{server}/prompts/resolve` | Retrieve prompt messages without inference |
| GET | `/openapi/v1.json` | Generated OpenAPI contract with bearer requirements |
| POST | `/v1/tools/query` | Filtered tool catalog, without executing tools |
| POST | `/v1/management/{domain}` | CLI plugin, skill or connector command; same token and configured policy |

Catalog discovery describes the feed, not an authorization list or a guarantee of account access. Only configured provider routes can be selected for execution. API errors expose safe problem details rather than provider exception messages or credentials.

Session transfer archives are limited to 384 KiB and include serialized model context in addition to public messages. Treat them as sensitive, unsanitized data. Import validates the version, message order, roles, lengths, state and provider route before admitting a new independent session. Current host instructions and tool policy remain authoritative; credentials, authorization grants and running jobs are not transferred. Active-session export/fork returns `409`. File transfer is separate from the host's automatic checkpoint storage and has a lower byte limit.

Metadata changes and rewind require an idle conversation. Archived sessions remain inspectable but reject new turns. Rewind checks the caller's expected current revision, commits a new revision and restores only conversation history. It does not undo workspace changes or refund token/turn counters. Recovered failed or canceled turns retain the last committed history and their accounted usage/failure counters.

`agent.maxTurns` bounds completed plus failed turns per conversation. `agent.maxTokens` bounds provider-reported input plus output tokens; once a turn has no reported usage, further turns fail closed while that cap is configured. Usage is not a billing estimate, and a single admitted turn may cross its token cap before the next admission check. Root cost caps, cross-session aggregate accounting and budget reservation are not implemented.

Plugin text routes share the same authentication/origin guards. These text routes accept no workspace-root override and cannot install, approve, enable or execute packages. The separate CLI management API can perform admitted package operations with the same bearer token. Expansion is bounded to the normal conversation input budget; the caller explicitly submits that text in a separate turn. See [plugins](/guide/plugins/) for activation and delivery limits.

Skill routes use the same authentication/origin checks and the host's fixed workspace. Invalid invocation data returns `400`, missing or unavailable skills return `404`, denied access returns `403`, and invalid source content returns sanitized `422` details. Expansion is capped at 64 KiB. It neither gathers context nor invokes isolated routes, tools or models. There is no HTTP skill-execution endpoint; submitting expanded text is a separate conversation action. Ordinary message turns can independently use admitted read-only skill functions through the model's normal tool loop. See [skills](/guide/skills/).

## Stream and Failure Semantics

The stream endpoint accepts the same text body as the JSON endpoint and returns `text/event-stream`. Events contain `turnId`, `sequence`, `kind` and optional `text`. A successful stream consists of `turn.started`, zero or more `text.delta` events and optional admitted hosted-activity events, then `turn.completed`. Hosted `hosted.call`, `hosted.result`, `hosted.listing` and `hosted.activity` events carry bounded JSON in `text`; they are informational, not requests for local execution. Sequence numbers order events within a turn; they are not durable replay cursors. Reasoning text is not published.

Each session allows one active turn. Overlapping messages and deletion while busy return `409`; this HTTP slice rejects overlap rather than implementing the planned queue/steer behavior. Global session/turn capacity returns `429`. Missing resources return `404`. Invalid text returns `400`; unsupported capabilities return `422`. A canceled or timed-out non-streamed turn returns `504`. Failed model operations return sanitized `502` problem details.

A turn runs against a detached framework-history snapshot. Local history commits only after the model finishes within bounds. A failure before that point retains the last committed conversation. After streaming headers are sent, failures end with `turn.failed` or `turn.canceled` when the connection remains writable; the HTTP status can no longer change. A disconnect can prevent delivery of the terminal event, including after a successful local commit. Read committed messages before retrying an ambiguous request. There are no idempotency keys, automatic turn retries or resume cursors.

Defaults cap input at 65,536 characters, generated text plus reasoning and internal skill-call arguments/results at 262,144 counted characters per turn, public history at 128 messages and 1,048,576 counted characters, and HTTP bodies at 512 KiB. Skill tool traffic stays internal and is not emitted as answer deltas. Unsupported response content is rejected. Reaching history limits requires a new session; there is no automatic truncation or compaction. These bounds are not aggregate token or billing guarantees. Cancellation and shutdown depend on the provider honoring its cancellation token.

## Delivery Boundary

Implemented: local bearer/origin protection, framework-backed text sessions with admitted read-only skill access, configured .NET model routes, bounded durable conversations, metadata/archive controls, conversation-only rewind, per-session reported token/turn admission limits, namespaced command discovery/expansion, JSON/SSE responses, cancellation, catalog APIs and authenticated OpenAPI. Shutdown cancels active turns and releases owned clients/store leases.

Pending: durable root coding orchestration, coding tools and their policy/approval workflow, automatic local MCP dispatch, sanitized transcript export and filesystem undo, aggregate usage/cost budgets, fallback/compaction, durable event replay, background-host discovery/pairing, server pipe/stdio/WebSocket transports, filesystem/process/VCS remote APIs, complete generated request clients and full-screen TUI surfaces. A separate CLI ACP text bridge translates stdio protocol messages into this HTTP API; it is not a new server transport or tool engine. Session/health DTOs and TypeScript API types are already generated from the checked-in canonical API and checked for drift.

Connector management uses the same bearer/origin protection and fixed workspace. Startup requires `{"confirmed":true}`; state changes require an idle session. Deletion of a session releases its connections. There is no unrestricted tool-call route. See [connector coverage](/guide/connectors/#capability-coverage) for the distinction between local connections, provider-hosted execution and external tunnel infrastructure.

See [testing](/development/testing/) for deterministic server checks. Local fixtures do not establish paid-provider account compatibility or production readiness.