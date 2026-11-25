---
title: "Skills"
description: "Scoped guidance, structured invocation and explicitly authorized skill execution."
---

# Skills

A skill is reusable text guidance with optional private resources, invocation parameters and an explicit execution recipe. The shared .NET catalog supplies CLI, runtime tools and the native editor. Skill text and collected results are untrusted model input. They cannot grant permissions or install executable code.

## Write a skill

Create `.code-assist/skills/change-audit/SKILL.md`:

```markdown
---
name: change-audit
description: Inspect a change for regressions and missing tests.
title: Change audit
hint: Review focus
audience: both
parameters: [target]
focus: ["src/**/*.cs"]
---
Review {{arg.target}} inside {{workspace}}.
Use nearby callers and tests as evidence. Separate defects from assumptions.
Task input: {{input}}
```

Place supporting text under that directory, for example `examples/checklist.md`. The catalog lists resources but reads each only when requested. A file such as `.code-assist/skills/change-audit.md` is also a skill; flat files have no private resource bundle. A directory containing `SKILL.md` is a bundle boundary, so its descendants are not discovered as separate skills.

| Field | Meaning |
| --- | --- |
| `name` | Optional ID, otherwise directory or flat-file name; 1 to 80 ASCII letters, digits, hyphens or underscores |
| `description` | Short discovery description, at most 1,024 characters |
| `title`, `hint` | Optional human-facing title and task-input prompt |
| `audience` | `both` by default, or `human`, `agent`, `none`; enforced on body and resource access |
| `parameters` | Up to 16 distinct ASCII identifiers, bound in declaration order or by name |
| `focus` | Up to 16 workspace-relative globs; agent invocation requires an explicitly supplied matching file |
| `route` | Optional configured role ID for isolated execution through `agent.delegate` |
| `engine` | Optional model selection for that route; invalid without `route` |
| `gather` | Up to four literal read-only tool requests, used only by `skill.run` |
| `license`, `compatibility`, `metadata` | Accepted descriptive fields; they do not affect runtime behavior |

Frontmatter is optional. Unknown fields fail with a diagnostic rather than silently accepting permission or execution directives. An empty YAML mapping is allowed. This is a native contract, not drop-in metadata compatibility with other products.

## Sources and identity

The first source owns a short ID. Discovery searches these sources in order:

1. The host's managed configuration directory, under `skills` (`managed`).
2. The current workspace's `.code-assist/skills` (`project`).
3. Ancestor `.code-assist/skills` directories, nearest first (`ancestor-1`, and so on), stopping at a Git boundary or 32 levels.
4. The CodeAssist home directory, under `skills` (`personal`).
5. The current workspace's `.agents/skills` (`project-shared`).
6. The current workspace's `.claude/skills` (`project-compat`).
7. The personal profile's `.agents/skills` (`personal-shared`).
8. The personal profile's `.claude/skills` (`personal-compat`).
9. Explicit `skills.directories` entries (`extra-0`, and so on).

Native `.code-assist` definitions take precedence over compatibility folders; managed definitions retain administrative precedence. Project sources precede personal sources within each group. Compatibility folders are scanned in the current workspace and personal profile, not every ancestor. They use the same bounded parser, audience checks, managed policy and lazy loading as native skills; unsupported metadata still produces diagnostics rather than granting permissions.

Default personal roots are `%USERPROFILE%\.code-assist\skills`, `%USERPROFILE%\.agents\skills` and `%USERPROFILE%\.claude\skills` on Windows, and `~/.code-assist/skills`, `~/.agents/skills` and `~/.claude/skills` on Linux. Personal compatibility folders are resolved beside the configured CodeAssist home. For example, `CODE_ASSIST_HOME=/profiles/work/.code-assist` selects `/profiles/work/.agents/skills` and `/profiles/work/.claude/skills`, keeping custom profiles isolated. Missing folders are ignored; discovery does not create them.

Use `project/change-audit` or `personal/change-audit` to select a shadowed definition. The catalog reports collisions and preserves origin-qualified selection. Approved plugin skills keep their package-qualified identities. Original in-memory guides write no startup files and run no model merely by being listed:

| Guide | Purpose | Audience |
| --- | --- | --- |
| `standard/risk-check` | Inspect regressions and missing safeguards | Human and agent |
| `standard/test-map` | Select focused executable checks | Human and agent |
| `standard/fault-trace` | Diagnose a reproducible failure before editing | Human only |
| `standard/proof-run` | Verify observable behavior through a representative workflow | Human only |
| `standard/project-map` | Build an evidence-backed project orientation | Human only |
| `standard/change-audit` | Review correctness and security risks without automatic edits | Human only |

The manual-only guides do not enter agent discovery. Existing audience, display and managed-policy restrictions apply to every guide. Invoke one in chat with `/skill/standard/fault-trace task`, or inspect it without inference with `code-assist commands expand skill/standard/fault-trace --input "task"`. See [conversation commands](/reference/cli/#conversation-commands) for structured named arguments and command namespaces.

Discovery is deterministic filesystem breadth-first traversal, not embedding, semantic or keyword search. Children are sorted by ordinal name. Grouped directories are searched up to four levels below each source. Explicit hidden source roots are admitted, but dot entries inside them, linked entries and common build directories are skipped. A directory containing `SKILL.md` ends traversal into that bundle. Root-level Markdown files are also admitted. Project sources resolving outside their owning scope are rejected. Discovery is bounded to 512 directories, 2,048 entries per directory and approximately 256 definitions. Same-source duplicate IDs and invalid metadata appear in diagnostics.

Metadata is rescanned on each catalog access; bodies load on demand. New files, removals and changed audiences are visible on the next access without reconstructing the catalog. There is no filesystem watcher or automatic read/edit activation. A host must change its workspace context to discover a nested workspace's own source. Already submitted conversation text is not retracted when a file changes.

## Invoke from CLI or editor

```sh
code-assist skills list
code-assist skills list --agent --json
code-assist skills expand project/change-audit --arg service-layer --input "Check retries"
code-assist skills diagnose --json
```

Repeat `--arg` for positional values. `--agent` applies model visibility and relevance checks; supply `--file src/Service.cs` when a skill declares `focus`. Human expansion intentionally bypasses relevance matching, not audience or managed authorization. Malformed sources and duplicate same-source IDs make `diagnose` exit with a configuration error; ordinary shadowing is informational.

These commands list or expand text. They do not invoke a provider, gather context, spawn children, run shell commands or change settings. In VS Code, **CodeAssist: Use Skill** selects admitted human guidance, prompts for declared parameters and task input, then sends expanded text through the normal conversation. It reads no editor source. That separate conversation can incur provider charges. See the [editor guide](/guide/vscode/).

## Templates and resources

Templates use `{{input}}`, `{{workspace}}`, `{{session}}`, `{{bundle}}`, `{{arg.target}}` and zero-based positional forms such as `{{arg.0}}`. Replacement runs once: values containing template syntax remain literal data. There is no shell tokenization, command substitution or executable markdown preprocessing. Missing referenced arguments and undeclared named values fail explicitly; the same declared parameter cannot be bound both positionally and by name.

Each input or argument is limited to 4,000 characters, with at most 16 positional and 16 named values. Expanded text is capped at 64 KiB and the call's file budget. Supplied values are also appended as labelled JSON data. Resource reads must remain inside a directory bundle and obey protected paths, strict UTF-8 and byte limits. Neither a `focus` glob nor a resource listing expands filesystem permissions. Bundles list at most 100 resources and scan at most 1,000 directories. Local body/resource reads are capped at 1 MiB or the smaller call budget; normal invocation has the lower expanded-text ceiling.

## Runtime loading and execution

`skill.load` is read-only. Use `{"list":true}` for the current scoped metadata list, `{"id":"change-audit","args":["service"],"files":["src/Service.cs"]}` to expand guidance, or `{"id":"change-audit","path":"examples/checklist.md"}` for a resource. Named `values` are an alternative to positional `args`. The advertised index has a byte budget; omitted entries remain discoverable through the explicit list operation. Listings remain bounded tool output, not an unlimited paging API.

Execution requires a separate authorized `skill.run` call. For example, a skill can declare:

```yaml
route: reviewer
engine: provider/model
gather:
  - slot: changes
    tool: workspace.changes
    json: '{}'
```

Add those fields to the frontmatter of a skill whose role and model are configured by the host. A `skill.run` request first checks that every gather target is available, read-only and not another `skill.*` tool. It sends each literal JSON object through the ordinary dispatcher. Gather arguments do not interpolate invocation values. Errors stop execution. Each result is capped at 16 KiB, labelled as untrusted context, and combined with guidance within a 60,000-byte prepared-prompt budget.

Without `route`, execution returns prepared text. With `route`, it invokes the ordinary `agent.delegate` tool through the dispatcher. Role tool restrictions, ancestry, model admission and child usage limits still apply. The linked execution deadline is 60 seconds; cancellation remains cooperative. The execution tool appears only when the host supplies an authorized dispatcher. Loading or human expansion never executes this recipe.

## Skills in conversations

`code-assist serve` connects the admitted catalog to ordinary server-backed conversations, including native VS Code chat. Tool-capable models receive bounded descriptions, declared parameters and file-focus metadata, not skill bodies. They can choose relevant guidance through `skill_load`, refresh metadata through `skill_list`, and read a listed text resource through `skill_resource`. Unrelated requests should not load skills. Selection is a model decision, not a guaranteed keyword trigger or filesystem watcher.

These provider-safe function names map to read-only `skill.load` access. The active role must admit that tool, `tools.disabled` must not disable it, and managed `tool.use` restrictions apply to its name and capability tags. A matching `ask` restriction blocks access because this conversation path has no approval surface. Catalog audience, visibility, relevance, protected-path and `skill.use` checks still apply to reads. Models explicitly configured with `tools: false` retain text-only conversations.

Each turn creates fresh skill access with at most 12 reads, a 128 KiB combined successful-output budget and a per-result ceiling of 64 KiB or `tools.outputMaxBytes`, whichever is smaller. The model function loop is capped at 12 iterations. Tool arguments and results count toward conversation limits, but are not published as answer text or public transcript messages. Internal framework history retains successful tool exchanges for subsequent turns; failed turns do not commit them.

Conversation functions do not run `gather`, start isolated routes, execute bundled scripts or enable workspace mutation. A skill can guide a text answer, but cannot make unavailable tools executable. Explicit human expansion remains a separate action. See [server behavior](/guide/server/) and [behavioral evaluations](/development/testing/#skill-behavior-evaluations).

## Preferences and managed admission

User settings can restrict discovery without changing files:

```json
{
  "skills": {
    "standardGuides": true,
    "entries": {
      "change-audit": { "display": "manual" }
    }
  }
}
```

`full` advertises descriptions, `compact` advertises the ID with an empty description, `manual` excludes agents but permits human selection, and `hidden` excludes both. The most restrictive matching short, qualified or origin-qualified preference wins. Equivalent transient variables include `Skills__Entries__change-audit__Display`, `Skills__StandardGuides` and `Skills__Directories__0`. Explicit source directories require user or managed trust; project configuration cannot register an arbitrary external path.

Managed configuration can disable local files or standard guides independently, or reject specific identities:

```json
{
  "policy": {
    "skills": { "localFiles": false, "standardGuides": false },
    "rules": [
      { "action": "skill.use", "resource": "team.audit/*", "effect": "deny" }
    ]
  }
}
```

Managed policy cannot be reopened by project, user or environment overrides. A matching non-allow `skill.use` rule fails closed, including `ask` when no approval surface exists. These admission checks apply again when loading, expanding or reading resources; a qualified alias cannot bypass a short-ID restriction. Policy settings are captured when the host constructs its catalog; source rescans are not a live settings-reload mechanism.

## Distribution and delivery boundary

Remote skill distribution uses [verified plugin packages](/guide/plugins/) and their existing registry, digest approval, dependency and managed policy paths. Plugin skill bodies follow the plugin contribution contract, not the local YAML recipe parser. `skills.registries` and `skills.lock` remain reserved; they do not activate an independent downloader. Trusted hosts can also construct `BundledSkillCatalog` with bounded in-memory text/resources.

Authenticated `GET /v1/skills/commands` and `POST /v1/skills/expand` provide human metadata and text expansion. The POST accepts `id`, `input` and positional `arguments`. Neither endpoint accepts a workspace-root override or exposes skill execution. See the [server API](/guide/server/).

Automatic cloud synchronization, separate remote skill catalogs, implicit shell expansion, metadata-granted tool permissions, arbitrary skill lifecycle scripts, activity-triggered session injection and instruction compaction/deduplication are not implemented. Opt-in behavioral evaluations cover representative synthetic tasks, not every authored skill or an enforced promotion gate. Local guards are not an OS sandbox, and deterministic fixture tests do not establish model quality.