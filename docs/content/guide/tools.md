---
title: "Tools"
description: "Tool schemas, risk, effects and component boundaries."
---

# Tools

The tool catalog describes executable tool components: names, descriptions, input schemas, risk, read-only status, and tags. The current CLI inspects that catalog; it does not run an agent or invoke tools from a conversation.

## Inspect the catalog

```sh
code-assist tools list
code-assist tools list --json
code-assist tools show fs.edit
code-assist tools show fs.edit --json
```

`tools show` prints the description and input schema. `tools list` applies the configured role's tool allow/deny selection. A filtered list is not proof that runtime authorization is implemented.

## Supported Tools

The tables below list every built-in tool by its catalog name. Optional tools appear only when their required service is enabled. Disabled tools and role restrictions can further reduce your available catalog. Run `code-assist tools list` to see what your installation exposes and `code-assist tools show <name>` to inspect its input schema. Catalog availability does not enable interactive CLI conversations.

### Files And Notebooks

These tools are included in the base catalog. File access still follows workspace protection and authorization rules.

| Tool | Description |
| --- | --- |
| `fs.read` | Read files and directories, with bounded text pages and supported image/PDF attachments. |
| `fs.find` | Find workspace files by glob pattern or search their contents using literal text or regular expressions. |
| `fs.diff` | Compare a file with proposed text or another file without changing either source. |
| `fs.write` | Write file contents through guarded filesystem operations. |
| `fs.edit` | Apply targeted text replacements to an existing file. |
| `fs.patch` | Preview or apply structured multi-file additions, edits, moves and deletions, with optional source-hash guards. |
| `notebook.edit` | Insert, replace or remove a Jupyter notebook cell while preserving metadata and clearing stale outputs. Does not execute cells. |

### Processes And Jobs

| Tool | Description | Availability |
| --- | --- | --- |
| `proc.run` | Run a command using a configured Bash or PowerShell profile with bounded output and execution time. Background execution requires jobs support. | Base catalog; configured shell required. |
| `job.list` | List the current session's background jobs and their status. | Jobs service. |
| `job.read` | Read a background job's status and bounded final result. | Jobs service. |
| `job.wait` | Wait for a job completion event for up to 120 seconds without stopping the job on timeout. | Jobs service. |
| `job.stop` | Cancel a background job owned by the current session and wait for cleanup. | Jobs service. |
| `job.notices` | Read durable job-completion notices or acknowledge a notice after handling it. | Completion-notice storage. |
| `output.read` | Read retained session output in line or character pages when the initial response was truncated. | Output storage. |

Running jobs do not survive a server restart. Stored completion notices and retained results can remain available.

### Code And Workspaces

| Tool | Description | Availability |
| --- | --- | --- |
| `code.query` | Look up definitions, references, hover information, document/workspace symbols, implementations and incoming/outgoing calls. | Configured language server. |
| `code.diagnostics` | Read current file diagnostics with source hashes, document versions and exact positions. | Language server with pull-diagnostics support. |
| `test.feedback` | List trusted test profiles, run one, read a saved report or find related tests by filename convention. | Configured test profiles. |
| `workspace.changes` | Inspect staged, unstaged, untracked and conflicted Git paths, optionally including bounded diffs. Never stages or discards changes. | Git change inspection. |
| `workspace.search` | Retrieve current source chunks with paths, line ranges and hashes. Supports local lexical matching and optional semantic ranking. | Retrieval service; semantic search additionally requires embeddings. |
| `workspace.branch` | List, create or remove managed Git worktrees without switching the current session. | Git worktree service. |
| `workspace.worktree` | Inspect, enter or exit the session's managed worktree, restore the original workspace and optionally request guarded cleanup. | Session routing and Git worktree services. |

### Sessions And Coordination

| Tool | Description | Availability |
| --- | --- | --- |
| `ask.user` | Ask questions with optional choices and free-text answers through an attached user interface. | User interaction service and an attached interface for answers. |
| `plan.todo` | Replace the session's todo list, tracking pending, in-progress, completed and cancelled work. | Todo storage. |
| `work.items` | Create, read, list, replace or remove durable session work items with prerequisites, owner labels and revision checks. | Work-item storage and an existing session. |
| `session.manage` | Rename a session or request workspace relocation at the next safe boundary. | Session management. |
| `session.plan` | Inspect or enter restrictive planning mode and submit a proposal for user review before leaving it. Approval does not grant tool permissions. | Planning service and user review to leave planning mode. |
| `agent.delegate` | Run a task in a role-restricted child session, resume an owned child or use background execution when jobs are enabled. | Delegation service. |
| `team.manage` | Create and inspect teams, invite or revoke sessions, join or leave, and close a team after members leave. | Team coordination and existing sessions. |
| `team.mail` | Send direct or broadcast messages, read private mail, acknowledge delivery and exchange cooperative shutdown requests. | Team coordination. |
| `knowledge.notes` | List, read, create, update or remove durable session, workspace or user notes. User-wide access requires consent. | Notes storage; user-wide access also requires user interaction. |
| `flow.run` | Compose tools into a bounded dependency graph with result bindings and reductions. Each child call is authorized separately. | Authorized tool dispatcher. |
| `output.submit` | Commit one schema-validated structured result for a session's output contract, with idempotent retries. | Explicit structured-output contract. |
| `config.manage` | Inspect masked effective settings and their origins. Update only permitted presentation preferences, not credentials or security settings. | Configuration management. |

### Web, Models And Media

| Tool | Description | Availability |
| --- | --- | --- |
| `web.fetch` | Fetch a web page and extract bounded text, retaining longer converted results when output storage is available. | Base catalog. |
| `web.search` | Search using a configured or user-selected provider and return titles, URLs and snippets. Does not automatically fetch results. | Search provider and any required endpoint. |
| `model.find` | Search published model metadata by provider and capabilities, page results or explicitly refresh the catalog. | Model catalog. |
| `media.render` | Generate or edit an image as a background job, optionally saving output with overwrite guards. | Compatible image model and jobs service. |
| `brief.publish` | Share a report and optional workspace attachments with a configured destination after explicit consent. | Sharing destination and user interaction. |

### Skills And Connected Services

| Tool | Description | Availability |
| --- | --- | --- |
| `skill.load` | List available skills, expand instructions with arguments or read a bounded resource from a skill bundle. Does not execute scripts. | Skill catalog. |
| `skill.run` | Execute a declared skill recipe with separately authorized nested tool calls. | Skill catalog and authorized dispatcher. |
| `tool.find` | Search visible tools by name, description or tag, optionally returning schemas and activating local schemas for later model turns. Does not grant permissions. | Tool discovery; connected-server search additionally requires MCP. |
| `mcp.auth` | Inspect connection status, authenticate with consent or disconnect a configured MCP server for the session. | MCP connections and user interaction. |
| `mcp.remote` | List or invoke tools on an authenticated session-owned MCP connection with schema validation and authorization. | Configured MCP connections. |
| `mcp.prompt` | List or retrieve prompt messages from a connected MCP server. Retrieval does not send them to a model. | MCP connections and prompt support. |
| `resource.list` | Discover resources and URI templates from connected servers. | Connected resource service. |
| `resource.read` | Read a resource by server and URI, returning bounded text or supported image/PDF attachments. | Connected resource service. |

Custom and connected tools can add names beyond this built-in list. They remain subject to normal catalog filtering and authorization.

### Browser

Browser tools require browser automation to be enabled and a supported browser installed. Each session uses an isolated context; page operations require an explicit tab ID. Available operations depend on the configured browser adapter. The supplied adapter supports:

| Tool | Description |
| --- | --- |
| `browser.tabs` | Open, list, focus and close tabs. |
| `browser.page` | Navigate, go back or forward, reload, wait for elements, inspect page snapshots, find elements and evaluate page scripts. |
| `browser.input` | Click, hover, drag, fill fields, select options, set checkboxes, press keys, scroll and respond to dialogs. |
| `browser.capture` | Capture screenshots, optionally saving them to guarded output paths. |
| `browser.files` | Upload workspace files or download files to guarded destinations. |
| `browser.inspect` | Read console/network activity, request/response details and metrics; capture traces, CPU profiles and heap snapshots. |

## Skills

`skill.load` lists current scoped metadata, expands one skill with positional/named values, or reads a bounded bundle resource. Audience and managed restrictions apply to every access. `skill.run` is a separate execution tool for declared read-only context and an optional isolated role; every nested call uses normal dispatcher authorization. Neither metadata nor templates grant permissions. Human CLI/editor expansion is text-only. See [skills](/guide/skills/) for formats, source precedence, limits and distribution.

## Diagnostics And Test Feedback

`code.diagnostics` requires an `ILanguageService` whose configured server advertises LSP pull diagnostics. It synchronizes the current source, requests a full document report, and returns a SHA-256, document version, one-based positions and paged findings (`offset` is zero-based; `limit` is 1–500). An unsupported server or incomplete response is an error, never a clean diagnostic result. This is document-scoped, not a workspace-wide compiler build or push-diagnostic collector. Hosts retain ownership of LSP processes and stream framing.

`test.feedback` supports `profiles`, `run`, `read` and `related`. Register `ITestFeedback` using `TrxTestFeedback` with a host-owned report directory, process runner and 1–32 `TestExecutionProfile` definitions. Profiles fix the executable, base arguments, runner kind and optional filter flag; model arguments cannot supply a command or report path. For example, an SDK 10 Microsoft.Testing.Platform profile can use:

```csharp
new TestExecutionProfile("unit", "dotnet",
	["test", "--project", testProject, "--no-progress"],
	MicrosoftTestingPlatform: true, FilterArgument: "--filter-class")
```

`testProject` is a trusted host-selected path. Microsoft.Testing.Platform applications need the `Microsoft.Testing.Extensions.TrxReport` package; the adapter appends `--report-trx`, `--report-trx-filename` and `--results-directory`. VSTest profiles instead append `--logger trx;LogFileName=report.trx` and `--results-directory`. The adapter does not auto-detect platforms or SDK 8/9 bridging syntax. Configure a compatible profile explicitly.

Runs normalize TRX case outcomes, messages and stack traces, retain bounded process output, and save a session-owned UUID report that can be read after restart. Missing, empty, cancelled, timed-out and failing runs never report completed success. XML DTDs/entities are prohibited; each report is capped at 8 MiB and 2,000 cases. There is no total-run retention quota or automatic cleanup. Running tests executes project/build code under the authorized process boundary, not an OS sandbox. `related` uses bounded filename conventions, not semantic test coverage or guaranteed impact analysis.

## Git Change Inspection

`workspace.changes` accepts `state: "all" | "staged" | "unstaged" | "conflicts"` and optional `includeDiffs`. It separates index and working-tree status/diffs and identifies untracked paths, original rename paths and conflicts. The current workspace must be the repository or worktree root; a nested directory cannot silently inspect its parent repository. Both sides of renames are checked against protected and ignored paths. Untracked files expose status only, not content.

Git invocation uses argument arrays, literal pathspecs, NUL-delimited status, and disabled external diff/textconv/fsmonitor execution. Truncated inventories fail instead of returning misleading partial status; bounded diffs label retained tails. Inspection never stages, commits, checks out or discards anything. Results are live observations, not an atomic repository snapshot or a replacement for process isolation.

## Scoped Notes

`knowledge.notes` supports `list`, `read`, `put` and `remove` in `session`, `workspace` or `user` scope. The host derives storage identity from the current session UUID or normalized workspace root; arguments never select a directory. Workspace notes are shared by sessions using that root; a different worktree root has a different scope. User scope belongs to the local host account, not an authenticated multi-tenant identity.

Creation uses `revision: 0` without an ID. Updating requires the current UUID and revision; removal also checks the revision. Writes atomically replace a scope snapshot under a process gate and exclusive writer lease. Conflicting writers fail and may retry after reading. Each scope holds at most 100 notes, with 120-character titles, 8,000-character bodies and a 2 MiB serialized ceiling. `list` returns metadata and supports a bounded substring query; `read` returns the body. Linked storage paths are rejected. There is no global scope-count quota or automatic expiry.

Every user-wide operation asks through trusted interaction; writes disclose the exact title/body. Dismissal or missing interaction denies access. Moving information into user scope therefore requires a separate approved write. Removal excludes the note from the current snapshot; it is not secure disk erasure. Notes are labelled untrusted data and never injected as system instructions, captured automatically, or allowed to grant permissions. Do not store credentials.

## Retrieval And Deferred Schemas

`workspace.search` accepts `query`, optional file/directory `path`, `limit` (1–20), and `strategy: "lexical" | "semantic"`. Default lexical matching is local token matching, not semantic search. Results include current source chunks, one-based line ranges, SHA-256 and scores. Protected/ignored paths are filtered before reading or embedding, and hashes are checked again before returning matches. Missing scopes fail explicitly; changed/deleted chunks are discarded.

Search rebuilds its bounded snapshot on each call; there is no persistent vector index. Limits are 1,000 scanned files/directories, 128 chunks, 128,000 source characters, 24 lines and 2,000 characters per chunk. Smaller configured file/search limits still apply. Oversized, binary or invalid UTF-8 files are skipped. `ScanComplete: false` marks exhausted scan/chunk budgets or stale source; `SkippedFiles` also reports skipped files/chunks, so even a complete scan is not proof every source byte was searched.

For semantic ranking, register `ITextEmbeddings`, for example `CodeAssist.Models.TextEmbeddingGenerator` around a trusted `IEmbeddingGenerator<string, Embedding<float>>`. Supply its provenance name and network destination; `null` destination asserts a fully local generator. The adapter compares real embedding vectors by cosine similarity, validates dimensions/finite values and rejects zero vectors. No provider, model, credentials or paid route is selected automatically. A declared remote destination adds network effects and requires trusted consent before any query/source upload. Tests use deterministic fixture vectors; they do not measure semantic model quality.

With standard DI, `tool.find` accepts `activate: true` to activate a local result page. On subsequent child model requests, `ChatClientAgentModel` sends initial read/discovery schemas plus the activated schemas, intersected with the current effective allowed tools. Discovery never changes permissions, ancestor restrictions, disabled tools or dispatcher authorization. Without visible `tool.find`, the adapter preserves the caller's full allowed schema list to avoid making tools undiscoverable. Direct construction can omit `IToolExposure` to retain eager schemas.

Exposure is in-memory only: at most 512 sessions, 24 activated tool names per session, a 96 KiB activation budget, and a 128 KiB/32-schema advertised budget. New activations evict oldest entries as needed; restarting or session-capacity eviction loses activation, not permissions or durable history. Remote discovery does not activate arbitrary server schemas: use `mcp.remote`. This is executable child-loop integration, not a completed root CLI conversation engine.

## Retained Results And Completion

`output.read` retrieves session-owned opaque output handles. Line pages use one-based `offset` and `limit`; long lines have a clipping notice and can be recovered using zero-based UTF-16 `characterOffset` and `characters` instead. Do not mix the two paging modes. Storage paths are never tool arguments. Standard dispatch supplies the store to nested workflows and child agents too.

MCP results, resource text and fetched page conversions retain their complete text when previews exceed output limits. Storage is bounded to 32 MiB per output, 128 files and 128 MiB per session. Capacity exhaustion fails explicitly; there is no automatic pruning or cross-process quota lock. Process capture retains up to eight million characters beyond the smaller displayed tail. If that capture ceiling is exceeded, the retained file explicitly identifies itself as a tail, not complete output. These limits are not an unlimited logging guarantee.

`job.notices` lists up to 100 unacknowledged completion notices or acknowledges a job UUID using `action: "ack"` and `id`. Standard jobs persist completion before publishing their completion event. Notices are session-owned, idempotently recorded and recoverable after reopening. Persistence failure marks the in-memory job failed rather than reporting durable success. Notices do not restart processes, replay effects or automatically resume a parent agent.

## Reads And Workspace Guidance

`fs.read` keeps its default text-file byte ceiling. Explicit `offset` or `limit` requests enable streaming pages for larger files, bounded to 64 MiB with cancellation, line clipping and a scan ceiling. Missing exact filenames may resolve to a unique Unicode-normalized sibling; ambiguous matches are not guessed, and resolved paths are checked again for protection.

Reads also declare and load nearby `AGENTS.md` guidance, bounded to 16 ancestor directories inside the workspace and a small shared content budget. Protected guidance and malformed UTF-8 are skipped. Guidance is labelled untrusted tool content, not system instructions or permission. This is not a global instruction manager or cross-session injection mechanism.

## Search And Delegation Selection

`tools.web.searchProvider: "searxng"` selects the configured SearXNG adapter. `"ask"` requests a trusted provider choice before any query is sent; SearXNG additionally requires `tools.web.searchEndpoint`. Hosts can register other `IWebSearch` implementations. The equivalent environment keys are `Tools__Web__SearchProvider` and `Tools__Web__SearchEndpoint`. All candidate provider destinations are declared before authorization. Selection, dismissal and disable are session-scoped, held only for the current host lifetime, and never written into settings. No answer means no network search.

`agent.delegate` selects an explicit request model, a role model or the active parent's model before falling back to the configured default. A resumed child may explicitly change models; selection is journaled while the original parent, role and effective tool restrictions remain fixed. Configured provider/model variants apply request-only header, body and option overlays. Convenience effort/sampling/thinking controls support specific protocols and fail explicitly elsewhere; protocol-specific JSON body overlays remain available. No variant mutates saved settings or expands permissions.

`plan.todo` replaces the durable session status list, including clearing it with an empty list. It supports pending, in-progress, completed and cancelled states, allows at most one in-progress item, and remains separate from work-item identities and background execution.

## Capabilities And Limits

The following tools are available as runtime components. CLI text conversations exist, but the complete coding-agent workflow remains pending.

| Capability | CodeAssist boundary |
| --- | --- |
| File and directory reads, image/PDF input | `fs.read` with explicit large-file pages, guarded normalized names and bounded untrusted workspace guidance; complete media normalization pending |
| Glob and literal/regex search | Combined `fs.find` component with result/deadline limits |
| Create, edit, multi-file add/update/move/delete | `fs.write`, `fs.edit`, structured transactional `fs.patch` components; formatters/checkpoints pending |
| Change previews and standalone comparison | Existing diff helper plus read-only `fs.diff` component |
| Foreground/background shell and result control | `proc.run`, job controls, bounded durable output handles and acknowledged completion notices; running-process recovery pending |
| Web fetch and current search | `web.fetch` with retained converted text; optional `web.search` with configured or trusted session-selected providers |
| Questions, todos and skills | `ask.user`, journal-backed `plan.todo`, scoped `skill.load` and authorized `skill.run`; human CLI/editor expansion and plugin distribution exist, durable instruction injection remains pending |
| Child role/model/resume/background delegation | `agent.delegate` with durable history/model switches, inherited models, configured variants, monotonic tool permissions and aggregate usage limits; running-job recovery and root actor remain pending |
| Resource discovery, templates and text/media reads | `resource.list` and `resource.read` plus official MCP SDK adapter and paging; automatic template expansion pending |
| Session rename/relocate and worktree create/list/remove | `session.manage` with durable events/deferred relocation; `workspace.branch` with real Git operations; actor integration and global active-root tracking pending |
| Model search, variants and cost metadata | `model.find` over published catalog metadata; configured variants/account availability are not reported as discovered |
| Tool composition and result processing | `flow.run`: bounded dependency graph, bindings and reductions, not arbitrary host JavaScript |
| Image generation/editing | `media.render` jobs plus explicit OpenAI SDK image clients; edit support must be declared; durable blobs/cost enforcement pending |
| Browser tabs/navigation/observation/input/evaluation | Session-isolated Playwright adapter; explicit tab handles, modal continuation and network authorization |
| Browser screenshots/transfers/console/network/traces/CPU/heap/audits | Capture, transfers, request/response inspection and profiles implemented; audits and native review conformance pending |
| Dynamic MCP/plugin tools | SDK imports, atomic refreshed snapshots, list-change notifications, validated structured/mixed results, session-owned OAuth and role-safe `mcp.remote`; declarative plugin packages are available, but automatic service startup and workflow-tool registration remain pending |

Checkpoint-backed rewind, session forks and automatic post-edit checks remain pending.

Every dispatched call has its input validated, effects planned and authorization checked before execution. Results have size limits. Hosts must supply an authorizer. Planning restrictions and explicit structured-output contracts are available; general policy evaluation, tool-approval surfaces, output contracts for arbitrary tools, blob retention and complete journal integration remain pending.

## Custom Tools

Custom tools use the same `ITool` contract as built-ins. An implementation provides a stable `ToolSpec`, a side-effect-free `PlanAsync` and `ExecuteAsync`. Plans declare every filesystem, process, network or logical state effect. Never execute commands, send requests or mutate state while calculating a plan. Custom .NET tools run as trusted host code, not in an extension sandbox.

Register a compiled implementation with DI in an application that already registers CodeAssist settings and host services:

```csharp
services.AddCodeAssistTool<MyCompanyTool>();
services.AddCodeAssistTools();
```

`AddCodeAssistTool<TTool>()` registers one singleton per implementation type and is idempotent. Tools must therefore be thread-safe and derive session identity from the per-call context, not mutable singleton fields. Multiple differently configured instances can be exposed through an `IToolSource` instead. For explicit composition:

```csharp
var source = new ToolSource([myTool]);
var catalog = ToolComposition.CreateCatalog(settings,
		new ToolPorts(Sources: [source]));
```

`ToolSource` snapshots the supplied list. Names must use 1-256 ASCII letters, digits, dots, hyphens or underscores; use a company namespace such as `acme.review`. Duplicate names fail startup rather than replacing built-ins. Descriptions, risk and tags are validated; schemas must be JSON objects no larger than 128 KiB. Disabling a custom name through `Tools__Disabled__0` uses the normal settings system. Role allow/deny entries match custom names/tags just like built-ins, and deny wins. An empty role allow list means all non-denied tools, not an empty catalog.

Dispatch validates JSON Schema before planning or authorization, using the MIT-licensed JsonSchema.Net 7.4.0 package. Draft 2020-12 is the evaluation fallback, formats are asserted, nested constraints and fragment-local references are supported. External references are rejected; bundle definitions into the schema instead of fetching them at runtime. Business validation still belongs in the tool parser. Do not rely on schema annotations such as `readOnly` to declare actual execution effects.

Create an `IToolDispatcher` with a role-scoped catalog and explicit `IToolAuthorization`. Standard composition can build one for nested workflows when an authorizer is registered; no allow-all authorizer is supplied. Direct `ITool.ExecuteAsync` calls bypass dispatcher schema checks, role selection and authorization. They are for trusted component callers and tests, not an agent API. Catalog inspection alone is not authorization.

Tool extension routes include compiled `ITool` implementations, supplied `IToolSource` collections and explicitly connected MCP tool sources. [Declarative plugins](/guide/plugins/) add digest-approved content, roles and dispatcher-backed sequences, not arbitrary executable code. There is no automatic project-directory assembly/script loading or hot-reload executable plugin manager. A project file cannot silently install or approve plugin content.

Custom-tool acceptance checks should cover invalid input before planning, denied execution without side effects, protected/outside effects, cancellation, bounded text/attachments, name collisions, disabled/role-hidden discovery and concurrent calls. Use Shouldly and `Should_ExpectedBehaviour_When_Condition`; HTTP integration fixtures use WireMock.Net.

## Durable Work Items

`work.items` supports `create`, `get`, `list`, `replace` and `remove`. It uses the current session only and requires a durable `SessionStarted` record. Tasks are not background jobs: changing an item to active does not start a process or agent.

```json
{
	"operation": "create",
	"title": "Verify patch behavior",
	"detail": "Check stale hashes and cancellation rollback.",
	"owner": "reviewer",
	"requires": []
}
```

Creation returns UUID, revision and `queued` state. `get` needs `id`; `list` accepts `offset` and `limit` (1-100, default 20). Replacement needs `id`, current `revision`, complete `title`/`detail`, `state` and optional `owner`/`requires`. Omitted owner/prerequisites clear those fields; read before replacing. States are `queued`, `active`, `done` and `cancelled`. Removal needs `id` and `revision`.

Titles fit 200 characters, detail 16,000, owner 120, prerequisites 100 unique UUIDs and a session ledger 500 current items. Prerequisites must exist in the same ledger. Self-links and cycles fail. Every prerequisite must be done before an item is active or done; reopening a prerequisite that would violate an existing dependent is rejected. Active items and referenced prerequisites cannot be removed. Owner is an organizational label, never an identity or permission grant.

Changes append checksummed journal events with optimistic sequence checks; unrelated append conflicts are retried up to four attempts. A stale item revision is never silently rebased. Removal preserves journal history. Reopening the ledger restores current items. Segment rotation, crash-tail recovery, cross-team task sharing, automatic assignment and user-defined metadata/hooks remain pending.

## Teams And Mail

`team.manage` supports `create`, `get`, `invite`, `revoke`, `join`, `leave` and `close`. Creation needs `name`; all other operations need `team`. Mutations other than creation need current `revision`. Invite/revoke additionally need a `member` session UUID. Existing sessions are required; the tool never fabricates an agent process.

Only the leader invites/revokes or closes. An invited session must call join itself; callers cannot supply another identity for join/leave. A team has at most 64 joined/invited sessions. The leader cannot leave, and close refuses while other members remain. Close revokes invitations and retains the team journal for audit; it is not forceful deletion or process shutdown.

`team.mail` supports `send`, `read` and `ack`, always with `team`. Send needs topic/text and exactly one `recipient` or `broadcast: true`; optional `replyTo` must identify a letter visible to the sender. Sender always comes from trusted session context. Topics fit 200 characters without controls; text fits 16,000. Broadcast snapshots joined recipients when sent, excluding the sender. Invitees cannot read or send mail until joined.

```json
{
	"operation": "send",
	"team": "11111111-1111-4111-8111-111111111111",
	"recipient": "22222222-2222-4222-8222-222222222222",
	"topic": "Patch review",
	"text": "Hash and rollback checks passed."
}
```

The UUIDs above are placeholders; use actual team and session IDs. Read uses journal `after` cursor and `limit` (1-100, default 20), returning only current session's inbox. Acknowledgment needs `letter`, is recipient-only and is idempotent. Reading does not acknowledge, and acknowledgment does not approve a plan or command. At most 1,000 sent letters are retained per team journal; existing journal byte/segment limits also apply.

Messages are untrusted data, not instructions with elevated authority. No remote socket/bridge addresses, implicit membership grants, background agent resumption or exactly-once UI notification delivery are advertised. Work ledgers remain session-local; team membership does not expose another session's tasks or provider credentials.

Optional mail `kind` is `text`, `shutdown.request`, `shutdown.accept` or `shutdown.reject`. Only the leader sends a direct shutdown request. A recipient's accept/reject must reply to that request and target its original sender; a second response is rejected. These are cooperative protocol messages, not permission grants or automatic process termination. A stable nonempty call ID retries an identical send idempotently; reusing it for different content fails.

## Planning And Structured Results

`session.plan` supports `get`, `enter` and `submit`. Submit needs a proposal of 1-16,000 characters and requests trusted user review. No caller-supplied `approved` flag exists. Missing/dismissed/rejected review keeps planning active. A changed planning revision during review invalidates approval; the caller must request review again. Approved proposal text is journaled.

`PlanningAuthorization` decorates, never replaces, the supplied authorizer. In planning it denies write/delete/process/network effects, except the tool's precisely scoped session planning controls. Reads and questions still require underlying policy approval. Standard composition wraps its internally created dispatcher when planning is supplied; a custom dispatcher must explicitly apply the decorator. Direct tool execution and other dispatchers are not automatically protected. There is no OS sandbox or cross-process session actor lock. Plan approval changes mode, not role permissions or shell allowlists.

An application that needs structured final data can explicitly supply:

```csharp
var output = new JournalStructuredOutput(journal, "review-v1",
		"""{"type":"object","properties":{"passed":{"type":"boolean"}},"required":["passed"],"additionalProperties":false}""");
var catalog = ToolComposition.CreateCatalog(settings,
		new ToolPorts(Output: output));
```

`output.submit` then exposes that schema directly as its input schema. The caller must create a real session and stable nonempty call ID. The sink validates again, checks UTF-8 output byte budget and commits one result per contract/session. An identical value retried with the same call ID returns the original sequence; a conflicting value or another call cannot overwrite it. A new task needs a new contract identity or session. Results survive reopening, but do not by themselves finalize an agent turn or validate arbitrary other tool outputs.

`job.wait` takes a job `id` and `seconds` (1-120, default 30). It subscribes before checking status to avoid a lost completion event and unsubscribes on every exit. Timeout returns current job status; cancellation ends only the wait, not the job. Completed results retain normal output/attachment limits. Restart recovery and unattended session wake-up are separate pending capabilities.

## Host-Backed Tools

### Configuration, Skills And Shells

`config.manage` supports `get`, `set` and `unset`. Read optionally selects an exact path or subtree and returns snapshot revision, values, origin tiers, editability and locks. Secrets and sensitive settings are masked. Set/unset require exact `path` and current `revision`; set also requires `value`. Only user-scope `appearance.animations` is editable. Environment and managed precedence still win, and environment values are never written to disk. A successful save does not promise that its value becomes effective.

`skill.load` accepts an `id`, optional `input` for instruction loading, or a bundle-relative `path` for resource reads. IDs must come from the configured catalog. Bundle listings use relative paths. Traversal, protected locations, links, binary resources and oversized files fail closed. Resources use strict UTF-8 and a maximum one-MiB byte budget, further restricted by tool limits. Loading never executes bundled scripts or grants capabilities.

`proc.run` accepts `shell: "default"`, `"bash"` or `"powershell"`. Configure executable paths through `tools.shells.bash` and `tools.shells.powershell`, or equivalent `Tools__Shells__bash` and `Tools__Shells__powershell` environment keys. Model arguments cannot select arbitrary executable paths. Missing profiles fail explicitly. On Windows, configure Git Bash explicitly if PATH would otherwise select WSL Bash. Requested timeouts cannot exceed the host limit. Both streams are pumped in bounded chunks, including giant lines without newlines. This is process control, not OS isolation or a complete shell-language policy.

### Sharing And Language Services

Supply `IBriefPublisher`, `IUserInteraction` and normal filesystem services to expose `brief.publish`. `HttpBriefPublisher` uses an explicit configured HTTPS endpoint; private HTTP requires host opt-in. The model supplies title, text and at most eight distinct unprotected workspace attachment paths, never a destination URL or approval flag. Trusted consent shows exact text and attachment paths before file reads or upload. Attachments share the configured byte budget. Multipart upload includes a stable session/call idempotency key; the remote server must implement deduplication. No token, notification or delivery guarantee is implied by a returned URL.

Supply `ILanguageService` to expose `code.query`. `StreamLanguageServer.ConnectAsync` initializes caller-owned input/output streams against an explicit workspace root. The host owns process startup, executable trust and termination. Queries synchronize full text using document versions and UTF-16 positions; tool input lines/columns are one-based. Operations are `definition`, `references`, `hover`, `documentSymbols`, `workspaceSymbols`, `implementation`, `incomingCalls` and `outgoingCalls`. Call-hierarchy preparation expands at most twenty candidates. Workspace files must remain unprotected and inside the root. Results are untrusted and bounded at the tool boundary; transport-level message limits and capability negotiation beyond position encoding require further hardening.

### MCP Authentication And Discovery

Supply `IMcpConnections`, normally `McpOAuthConnections` with explicitly named `AuthenticatedMcpServer` entries, to expose `mcp.remote`; supplying trusted interaction also exposes `mcp.auth`. Authentication supports `status`, `connect` and `disconnect`. Connect requires a trusted `Authenticate` choice. The host creates official SDK `ClientOAuthOptions`; use `AuthorizationCallbackHandler`, not the obsolete redirect delegate. The SDK validates PKCE state and optional issuer. `LoopbackOAuthCallback` can receive browser callbacks; the host supplies browser opening and a loopback redirect port. Tokens and authorization codes never appear in tool arguments or results. Persistent token caches must use host-owned secure storage; no plaintext cache is created automatically.

Connections belong to the current session. A disconnected transport must be explicitly reconnected, with trusted consent. `tool.find` optionally accepts `server` to search authenticated metadata without initiating login. Results use the same role/disabled filtering as invocation and include an explicit `mcp.remote` call hint. The host must supply `DiscoveredToolVisible` for direct composition; settings-aware composition supplies it. No visibility policy means remote discovery/invocation fail closed. Listing metadata does not authorize the eventual network/execution effects.

### Worktrees And Delegation

`workspace.worktree` supports `get`, `enter` with `name`, and `exit` with optional `remove`. It requires an existing durable session. Standard dispatch resolves the active root before each call and serializes same-session transitions; nested flows can dispatch normally but cannot switch worktrees mid-flow. Custom dispatchers must apply `WorkspaceToolDispatcher` themselves. Exit restores the original root before cleanup, and a cleanup failure retains retryable state. `remove: false` retains the directory. Removal refuses modified/untracked files and commits not reachable from the original workspace HEAD, even when generic branch removal requests force. Active-root tracking covers sessions loaded in this process, not all durable sessions or other processes. A crash between worktree creation and its journal commit can still leave an owned worktree requiring reconciliation.

`JournalDelegation` executes child model steps through `IAgentModel`; the default `ChatClientAgentModel` uses configured providers and normal settings. Child tools pass through the shared authorized dispatcher. Effective tool names are persisted and only narrowed across descendants and resumes. Resume requires the original parent, role and model; incomplete historical tool effects require explicit reconciliation and are not replayed. Legacy child records without an effective-policy snapshot fail closed. Depth, model-step and aggregate token usage are bounded across a root's delegation tree. A cost ceiling requires a model that reports verified costs; the default adapter refuses such runs rather than inventing cost data. An over-budget reply is recorded and blocks subsequent effects, but cannot undo the provider request already made. Accounting serialization is process-local, not a cross-process billing guarantee. Background execution uses optional in-memory jobs; root interactive `chat`/`run`, durable job completion and multimodal child history are not delivered by this service.

## Unavailable Workflows

Scheduled prompts, remote triggers, persistent interpreter sessions, remote teams and durable sharing notifications are not supported. `flow.run` executes a declarative dependency graph, not an interactive interpreter. `job.wait` waits for an existing job; it does not schedule future work or resume an unattended session.

Language-server processes must be supplied by the host. Worktree ownership tracking is limited to sessions loaded in the current process. OAuth persistence depends on the configured secure cache and platform; unattended authentication is not guaranteed.

General approvals, conversational workspace tool execution, running-job recovery and durable media retention remain unavailable. Text-only CLI `chat`/`run` does not enable these workflows.

## Optional Services

SearXNG requires an endpoint whose instance enables JSON responses:

```json
{
	"tools": {
		"web": {
			"searchProvider": "searxng",
			"searchEndpoint": "https://search.example.org/search"
		}
	}
}
```

Equivalent environment keys are `Tools__Web__SearchProvider` and `Tools__Web__SearchEndpoint`. Private-host access remains disabled unless explicitly enabled. Results are untrusted; search does not automatically fetch result pages.

Local skills come from `skills.directories`. Each skill uses `SKILL.md`, optional YAML `name`/`description`, an instruction body and bundled files. Loading does not execute scripts or change permissions. Remote registries and pinned installation are not implemented.

The question broker exposes pending questions and answer/dismiss operations to an attached host surface. Without a subscriber, questions return dismissed rather than inventing answers. Journal-backed session/todo calls require an existing `SessionStarted` record. Relocation requests do not mutate a running tool context; the session owner must apply them at a safe boundary.

Browser automation requires an explicitly created `PlaywrightBrowser` and installed Chromium or a supported browser channel. Each session gets an isolated context without a personal profile. Page requests pass through the supplied authorizer and guarded HTTP handler. This is not an OS sandbox or a guarantee against every browser network mechanism. Accessibility/performance audits are not advertised operations.

## Read-Only Comparison

The `fs.diff` component compares an existing UTF-8 file with either proposed `content` or another existing `otherPath`. Supply exactly one comparison source. It declares reads for every input path, checks input size/type and bounds the result without changing files. `contextLines` accepts 0 through 20, defaulting to 3. Changed-content results include the original byte SHA-256 for a subsequent guarded edit. Trailing blank lines and missing final newlines are represented in the preview.

```json
{
	"path": "src/example.txt",
	"content": "first\nnew value\n",
	"contextLines": 3
}
```

Inspect the schema with `code-assist tools show fs.diff`. Diff text is a bounded preview, not a patch input codec.

## Notebooks And Discovery

`notebook.edit` operates on an existing UTF-8 `.ipynb` with notebook format 4 and a cells array. It preserves notebook/cell metadata and existing IDs, creates IDs for inserted cells in format 4.5+, and clears outputs/execution counts for edited code cells. It does not execute a kernel. `expectHash` checks source bytes; `dryRun` previews without writing. File size, path protection, cancellation and atomic writes use the same services as filesystem tools.

```json
{
	"path": "analysis.ipynb",
	"operation": "replace",
	"index": 0,
	"source": "print(42)",
	"kind": "code",
	"dryRun": true
}
```

`tool.find` supports text/tag queries, `offset`, `limit` (1 through 50) and `includeSchemas`. Settings-aware composition restricts results to the current role and excludes disabled tools; unknown roles receive no results. A raw tool catalog needs an explicit visibility provider before registering discovery. Discovery does not execute, authorize or automatically activate a tool.

## Effects and risk

Tool contracts describe intended effects separately from execution. The dispatcher supplies these effects to its authorizer before executing reads, writes, process execution, or network operations.

Current component guards and settings trust rules are not an OS sandbox. Agent-time policy evaluation, approval UI and the complete session pipeline remain pending. A question broker is not an approval engine. Do not treat a configured permission preset as an active execution boundary or direct `ExecuteAsync` calls as authorized dispatch.

## Structured patches

The patch component uses structured add/update/delete/move operations, with ordered text replacements for updates and optional edits during moves. Every operation is computed before writes begin. A failure or cancellation rolls back completed steps; rollback failures are reported. This is not an OS-level multi-file transaction or durable crash recovery.

Use `dryRun: true` to validate and preview without writing. Dry-run plans declare only reads. Optional `expectHash` on update/delete/move rejects a source that no longer matches its earlier SHA-256; a failed guard prevents the whole patch. Guards do not lock out external editors. Applied results contain bounded per-file diffs, including removed text and `/dev/null` deletion headers.

```json
{
	"dryRun": true,
	"operations": [
		{
			"op": "update",
			"path": "src/example.txt",
			"expectHash": "<source-sha256-from-preview>",
			"edits": [{ "oldString": "old value", "newString": "new value" }]
		},
		{ "op": "add", "path": "notes.txt", "content": "Change recorded.\n" }
	]
}
```

Replace the placeholder hash with the actual preview hash and set `dryRun` to false to apply. Inspect the complete input schema with `code-assist tools show fs.patch`. Supply the documented JSON operations; a truncated preview may omit changed lines.

See [architecture](/development/architecture/) for ownership and [feature status](/guide/features/) for missing runtime pieces.
