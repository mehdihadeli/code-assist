---
title: "Feature status"
description: "Implemented components and work that is still pending."
---

# Feature Status

The CLI supports configuration, catalogs, connector management, diagnostics, streamed one-shot prompts, line-oriented chat and explicit attached-session controls. The VS Code extension adds native text conversations and selection questions. The tables below distinguish available runtime components from workflows that are not yet available in either interface.

Status definitions: **Implemented** means executable behavior exists and is covered by local checks; **Partial** means a narrower capability exists; **Not implemented** means no built-in support. Generic model instructions or future extension contracts do not count as implemented product features.

## Delivered Components

| Capability                                 | Status      | Current boundary                                                                                                                 |                             |
| ------------------------------------------ | ----------- | -------------------------------------------------------------------------------------------------------------------------------- | --------------------------- |
| .NET 10 CLI packaging                      | Implemented | `PackAsTool`; local package, not published to NuGet                                                                              |                             |
| Spectre CLI with Generic Host DI           | Implemented | One host/container and command-group registration                                                                                |                             |
| CLI text conversations and session controls | Implemented | `run`, bounded text attachments, line-oriented `chat`, JSON-line events, explicit attachment, session selection/fork/transfer; durable committed recovery, metadata/archive and conversation-only rewind; no coding tools | |
| Shared conversation commands and Spectre components | Implemented | Reserved native operations, namespaced configured/skill/plugin guidance, one-shot text-only list/expand, shared transcript/input/help/data views; not a full-screen UI | |
| ACP text bridge | Partial | Version-1 initialization, workspace-bound text sessions, history load/resume/list/delete, streaming and cancellation over stdio through the shared HTTP client; client MCP injection, tools/approvals, media, modes and full conformance pending | |
| Explicit API request command | Implemented | Authenticated loopback method/path/JSON operations with deadlines, byte limits, no redirects and sanitized errors; not operation-ID resolution or event streaming | |
| Layered settings and provenance            | Implemented | Trust tiers, tightening, managed locks, validation, schema and JSONC editing                                                     |                             |
| Environment overrides and secret handling  | Implemented | `__` nesting, dictionary/list support, no persistence, API keys from environment only                                            |                             |
| Settings inspection and diagnostics        | Implemented | `config`, `doctor`, `inspect paths` and safe role metadata; no inference request                                                 |                             |
| Provider/connector management              | Implemented | Configured session-owned MCP connections, CLI and consented HTTP/native management; no automatic editor MCP tool dispatch |                             |
| SDK provider adapters                      | Implemented | OpenAI chat/Responses/compatible, Anthropic, Azure, Gemini, Vertex and Bedrock components                                        |                             |
| Shared live model/provider catalog         | Implemented | models.dev, bounded ETag cache, metadata/settings overlays, manual refresh; no bundled snapshot                                  |                             |
| Catalog routing and capability enforcement | Implemented | Explicit overrides win; known tools/media/output limits checked for response and streaming calls; unknown metadata stays unknown |                             |
| Pure tool contracts and text algorithms    | Implemented | Effects/plans, argument validation, edit/diff/output/command/HTML helpers                                                        |                             |
| Filesystem/process/web tool components     | Implemented | Read/find/diff/write/edit/structured transactional patch, foreground/background process adapters, bounded web fetch; no agent policy loop |                             |
| Background job components                  | Implemented | Session-scoped controls, event-driven waits, durable completion notices/acknowledgements and bounded output handles; running jobs are not recovered |                             |
| Custom tool registration and input schemas | Implemented | Typed DI tools, immutable source collections, collision checks and offline JSON Schema validation before planning; no script/assembly autoload or sandbox |                             |
| Durable work-item ledger                   | Implemented | Session-local CRUD, owner labels, revision guards, prerequisites and cycle/readiness checks; not a shared team board |                             |
| Team coordination and mail                 | Partial     | Durable invitation/join/leave/close, recipient-private direct/broadcast/reply mail and receipts; no automatic agent spawn/resume or remote transport |                             |
| Restrictive planning mode                  | Implemented | Durable state, trusted user review, stale-review guards and non-elevating authorization decorator; requires authorized dispatch |                             |
| Structured result submission               | Implemented | Explicit caller-supplied schema, byte limit, durable once-per-contract/session commit and idempotent retry; not automatic turn completion |                             |
| Resource discovery/read adapters           | Implemented | Official SDK transport, atomic tool refresh/list-change handling, validated structured/mixed results, custom-URI reads and retained text |                             |
| Session-owned MCP OAuth                    | Implemented | Trusted SDK callbacks, PKCE/state, loopback browser authorization, bounded elicitation and encrypted Windows cache; non-Windows persistence/native interaction UI pending |                             |
| Callable configuration and sharing         | Implemented | Narrow revision-guarded non-security settings edits; optional configured report uploads with exact-text consent and bounded attachments |                             |
| Language intelligence adapter              | Implemented | Eight real LSP operations plus paged versioned pull diagnostics/source hashes; host owns configured streams/process lifecycle |                             |
| Structured test feedback                   | Implemented | Trusted MTP/VSTest profiles, bounded TRX normalization and session-owned reports; explicit host registration/reporters required; related discovery is filename-based |                             |
| Git change inspection                      | Implemented | Read-only staged/unstaged/untracked/conflict inventory and bounded diffs with protected rename checks; repository-root scope required |                             |
| Scoped durable knowledge                   | Implemented | Session/workspace/user notes, atomic revision-checked replacement/removal and trusted user-wide consent; no automatic instruction injection or secret capture |                             |
| Workspace retrieval                        | Implemented | Bounded current hash-checked chunks; local lexical matching and optional real embedding-vector ranking with remote-upload consent; no persistent index |                             |
| Deferred tool schemas                      | Implemented | Session-local activation through role-filtered discovery and actual child model request selection; permissions remain unchanged, root actor pending |                             |
| Child delegation runtime                   | Implemented | Durable history/model switches, inherited parent models, configured variants, same-parent/role resume and monotonic tool permissions; not a root CLI conversation engine |                             |
| Tool catalog queries                       | Implemented | Host/SDK facades, CLI inspection and role-safe `tool.find`; not conversational agent execution |                             |
| Local interaction/skill/search services    | Implemented | Durable todo replacement/clear, bounded workspace guidance, local skill resources and configured or trusted session-selected search; no permission from content |                             |
| Scoped skills and explicit execution      | Implemented | [Skills](/guide/skills/): lazy scoped discovery, qualified collisions, audiences, structured templates/resources, restrictive preferences, managed admission and authorized gather/delegation; human CLI/editor expansion is text-only |                             |
| Session/worktree/model tools               | Implemented | Durable rename/relocation requests, session worktree enter/restore/guarded cleanup and shared published-model discovery; no global cross-process active-root registry |                             |
| Authorized declarative workflows          | Implemented | Dependency graph, bindings, reductions, read concurrency and mutation serialization; requires supplied authorizer |                             |
| Image generation/editing adapter           | Implemented | Explicit OpenAI SDK clients/capabilities and guarded background jobs; durable blobs and cost budgets pending |                             |
| Isolated browser adapter                   | Partial     | Playwright tabs/pages/input/dialogs/capture/transfers/diagnostics; audits and native review conformance pending |                             |
| Protocol/SDK foundation                    | Partial     | Catalog/tool descriptors and in-process facades, not a complete session API or transport client                                  |                             |
| Framework-backed HTTP sessions             | Implemented | Microsoft Agent Framework over IChatClient, local bearer/origin protection, bounded history, JSON/SSE, cancellation, catalog and OpenAPI; no workspace tool execution |                             |
| Native VS Code text client                 | Implemented | Native chat, command/selection fallback, session/history/model controls, managed/external loopback connections, trust and SecretStorage; coding tools and durable replay pending |                             |

## Pending Runtime and Surfaces

| Capability                                         | Status          | Current boundary                                                                           |
| -------------------------------------------------- | --------------- | ------------------------------------------------------------------------------------------ |
| Interactive coding loop | Not implemented | Native slash commands and prompt/skill/plugin expansion exist; workspace coding tools and approval execution remain pending |
| Runtime policy, approvals and questions            | Partial         | Input-validated dispatcher calls supplied authorizer; planning restriction and trusted review work; general policy/approval UI pending |
| Journals, sessions, resume/export/rewind/fork      | Partial         | Workspace-scoped committed recovery, retained snapshots, metadata/archive, conversation-only rewind, bounded transfer and independent forks; coding actor, event replay, file undo and projections pending |
| Compaction, retry/fallback, aggregate budgets      | Partial         | Root per-session reported token/turn admission limits and persisted usage; child usage/cost accounting; cross-session totals, root cost reservation, compaction/retry/fallback pending |
| End-to-end token streaming                         | Partial         | Official SDK through Agent Framework and HTTP SSE to CLI text/JSON-line output and native VS Code; durable replay remains pending |
| Named variants and local model discovery           | Partial         | Configured variants apply to delegated and HTTP session requests; local inventory discovery remains pending |
| Daily catalog scheduling                           | Not implemented | Manual refresh and cache behavior exist                                                    |
| Instructions, skills, roles and delegation runtime | Partial         | Bounded read-local workspace guidance, skill resources, role-filtered dispatch and durable child loop; global instruction management and root actor remain pending |
| MCP connections/resources/prompts/OAuth            | Partial         | [Configured lifecycle, prompts, OAuth and hosted toolsets](/guide/connectors/) exist; automatic editor local-tool dispatch, richer interaction, hosted batches and tunnel lifecycle remain pending |
| Durable jobs and background delegation runtime    | Partial         | Bounded durable output and acknowledged completion notices exist; process recovery, automatic parent wake-up and client notification surfaces remain pending |
| Session/worktree/model workflow integration       | Partial         | Delegated model inheritance/variants and worktree routing exist; safe-boundary root actor, global active-root tracking and account availability remain pending |
| Workflow/media runtime retention                  | Partial         | Executable components exist; complete policy/journal pipeline, durable media blobs and aggregate cost enforcement pending |
| Hooks, formatters and plugins                      | Partial         | [Declarative plugins](/guide/plugins/): verified disabled installs, digest approval, dependencies, registries, namespaced guidance/roles and bounded dispatcher hooks/workflows. Configured process hooks, formatters, service auto-start and behavioral evals remain pending |
| Vault/OAuth login and Foundry project route        | Not implemented | Environment/native cloud credentials and compatible endpoints are separate supported paths |
| Host service, discovery and transports             | Partial         | HTTP text service/OpenAPI and private editor child lifecycle exist; shared discovery/leasing, pipe/stdio and generated clients pending |
| Full-screen TUI and editor coding workflows        | Not implemented | Workspace editing, tool approvals and durable conversation replay are not yet available in the clients |
| Browser, notebook, LSP and IDE integrations        | Partial         | Playwright browser, v4 notebook editing and eight-operation LSP adapter; audits, notebook kernels, LSP process hosting and IDE surfaces remain pending |
| OS sandbox and enterprise audit guarantees         | Not implemented | Component guards and settings trust are not process isolation                              |
| Automatic runtime installation and updates         | Not implemented | Local CLI package and VSIX builds are separate from automatic installation/update delivery |

## Current Limitations

Committed text conversations recover by default from a workspace-scoped store with an exclusive service lease. The CLI-owned service still stops on command exit; concurrent clients must explicitly attach to one running service. Checkpoint rewind restores conversation history only. Durable coding execution, stream replay and the full-screen terminal interface remain unavailable. Runtime tool components do not mean those tools can be used from current chat interfaces.

Explicit archive export/import transfers committed context separately from automatic checkpoint persistence. ACP supplies a tested text bridge, not full protocol or coding-agent conformance. Accounts, shared-service discovery/reconnect, live runtime reload, aggregate usage/cost reporting, plugin update orchestration and installation updates remain unfinished.

Provider compatibility depends on your configured endpoint, credentials and model capabilities. Local tests do not verify your live account or billing limits. See [server limits](/guide/server/#delivery-boundary), [tool capabilities](/guide/tools/#capabilities-and-limits) and [testing](/development/testing/) for details.
