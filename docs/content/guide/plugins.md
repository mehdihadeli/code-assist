---
title: "Plugins"
description: "Create, inspect, approve and use declarative plugin packages across CLI and VS Code."
---

# Plugins

CodeAssist plugins are declarative packages, not assemblies, scripts or package-manager dependencies. A package explicitly lists its guidance, roles, bounded tool sequences and supporting assets in `assist-plugin.json`. The shared .NET runtime owns verification and activation; VS Code consumes enabled text through the authenticated local server.

## First Package

Use a new directory and a lowercase dotted identifier:

```sh
code-assist plugins init ./review-plugin --id team.review
code-assist plugins validate ./review-plugin --json
code-assist plugins pack ./review-plugin --out ./review-plugin.zip
code-assist plugins install ./review-plugin.zip
code-assist plugins list --json
```

Installation does not grant user approval. Inspect its manifest, declared files and `digest` from the listing, then explicitly approve that exact digest. An administrator can separately require an exact reviewed content digest, as described under Organization Policy:

```sh
code-assist plugins enable team.review --approve SHA256_FROM_LIST
code-assist plugins expand team.review/inspect --input "Review the parser change"
code-assist plugins stats --json
code-assist plugins disable team.review
code-assist plugins remove team.review
```

Replace `SHA256_FROM_LIST` with the full installed digest. `expand` prints text and records its use; it does not call a model or execute tools. In VS Code, **CodeAssist: Run Plugin Prompt** selects enabled guidance, asks for task input, and submits the expanded text through the normal conversation. That last action can incur provider charges. It does not read editor source automatically.

`enable` and `disable` accept `--workspace` to set a decision for the CLI's current workspace rather than the user default. Decisions live under user state using a hashed canonical workspace root, never in committed project configuration. Removal is user-wide.

## Manifest

The scaffold creates release `0.1.0`, one prompt and its Markdown file:

```json
{
  "schema": 1,
  "id": "team.review",
  "release": "0.1.0",
  "summary": "Scoped review guidance",
  "requires": {},
  "contributions": [
    {
      "key": "inspect",
      "kind": "prompt",
      "entry": "guidance/inspect.md",
      "description": "Inspect a scoped code change",
      "tools": ["fs.read", "fs.find"]
    }
  ],
  "assets": []
}
```

Contribution names are qualified as `team.review/inspect`. IDs contain two to four nonempty lowercase dotted segments. Releases are exact stable `major.minor.patch` values, without prerelease tags or ranges. Contribution keys are unique within a package.

| Kind | Entry | Delivered Behavior |
| --- | --- | --- |
| `skill` | Markdown | Lazy loading through the shared skill catalog; only declared assets can be read |
| `prompt` | Markdown | Explicit bounded text expansion with task input marked as untrusted data |
| `role` | Markdown | Namespaced delegatable role; requires a declared tool restriction and inherits parent restrictions |
| `style` | Markdown | Explicit text expansion, not an automatic global style override |
| `workflow` | JSON | Bounded declarative sequence through a caller-supplied authorized dispatcher |
| `hook` | JSON | `tool.before` or `tool.after` sequence on composed tool dispatch; cannot grant permissions |
| `mcp` | JSON | Validated service definition; automatic connection and process launch are not delivered |
| `language` | JSON | Validated service definition and extension mappings; automatic LSP startup is not delivered |

Only declared files are packed. An undeclared local `.env`, build output or script is not included. Declaring a secret file as an asset still publishes it: inspect the asset list before distributing a package. No install scripts, shell interpolation, JavaScript evaluation or DLL loading occur.

Paths must be relative, portable and free of traversal, drive prefixes and backslashes. Archives reject duplicate case-insensitive paths, undeclared entries and non-regular file modes. Package loading rejects detected symlinks/junctions within the chosen package root. Limits include 128 KiB per manifest, 1 MiB per file, 16 MiB per package and 64 MiB total installed package content, with at most 128 installed plugins. These checks are not an OS sandbox or protection against every filesystem race.

## Requirements and Updates

Declare exact requirements using `"requires": { "team.base": "1.0.0" }`. Registry installation resolves the closure, verifies archive checksums and manifest identities, then installs the batch disabled. Missing releases, conflicts and cycles fail before active state changes. A direct directory/ZIP install requires its dependencies to be installed already.

Approve and enable dependencies before their dependants. A required plugin cannot be disabled while an enabled dependant needs it, or removed while any installed package requires it. Workspace overrides must also preserve the dependency graph.

To update, increment the manifest release, validate/pack it and run `plugins install` again. A release cannot be replaced with different content. Changed content revokes both user activation and workspace overrides; review and approve the new digest. Identical content from the same source preserves the existing decision. Changing an installation's source also revokes user approval, even when its bytes match. Updates and downloads never happen at startup.

## Registries

Publish an inspected package to an explicit local directory:

```sh
code-assist plugins publish ./review-plugin --registry ./registry --name team.library
code-assist plugins registry ./registry --json
code-assist plugins install ./registry --id team.review --release 0.1.0
```

Publication writes immutable ZIP releases and `assist-registry.json` with archive SHA-256 checksums. It does not upload, tag Git, register a persistent source or publish to a cloud marketplace. Serve the resulting directory separately when remote distribution is required.

`registry` and registry-based `install` also accept an explicit HTTPS catalog URL. Archive paths are relative to that catalog and stay on its origin. Redirects, embedded credentials and private-network resolution are rejected. Resolution has package/depth, transfer and expanded-content budgets. Omitting `--release` explicitly selects the highest listed stable version within the chosen release lane for this invocation; dependencies remain exact pins.

A digest proves reviewed bytes match, not that a publisher is authentic or safe. Check the registry source and package content yourself. Publisher signatures, curated feeds, unattended updates and remote publishing are not implemented.

## Hosting and Discovery

Publish packages locally, then serve the entire output directory with a static HTTPS host. The catalog and its `releases/` archives must stay together, at the same origin. No server-side plugin execution or package manager is required. Catalog downloads are capped at 128 KiB; archives are capped at 17 MiB, with a 30-second download deadline and no redirects. A resolution transfers and expands at most 64 MiB. Private-network HTTP hosts are intentionally unavailable. There is no built-in bearer-header, credential-helper or Git-clone integration; distribute private packages through a protected local registry or pre-provisioned user state instead of putting credentials in URLs.

Named libraries are user/managed settings, not repository-supplied trust declarations:

```json
{
  "pluginLibraries": {
    "engineering": { "source": "https://plugins.example.com/assist-registry.json" }
  }
}
```

The transient equivalent is `PluginLibraries__engineering__Source`. Relative local paths resolve from the current working directory. Listing aliases never accesses their sources; opening or searching one performs an explicit fresh catalog read:

```sh
code-assist plugins libraries --json
code-assist plugins registry engineering --json
code-assist plugins search engineering --query "review" --json
code-assist plugins install engineering --id team.review
code-assist plugins diagnose --workspace --json
```

`plugins validate DIRECTORY` recognizes a directory containing `assist-registry.json`; that named file can also be validated directly. Registry validation checks metadata only. Installation additionally verifies archive bytes, package identities, component definitions and the dependency graph before changing installation state. Catalog entries cannot add or replace package contributions. The package manifest remains the sole component authority.

## Release Lanes

One registry can carry separate release lanes without duplicating registry identities:

```sh
code-assist plugins publish ./review-plugin --registry ./registry --name team.library --lane pilot
code-assist plugins search ./registry --lane pilot --json
code-assist plugins install ./registry --id team.review --lane pilot
```

The default lane is `general`. Lane names start with a lowercase letter and contain at most 32 lowercase letters, digits or hyphens. Every dependency must have its exact required release in the same lane. There is no fallback to another lane. An id/release pair remains immutable and unique across the registry; publish a distinct release for another lane. A lane is an explicit lookup filter, not an update scheduler or automatic permission grant.

## Local Recommendations

Registry entries may carry bounded, non-executable discovery metadata:

```json
{
  "schema": 1,
  "name": "team.library",
  "releases": [
    {
      "id": "team.review",
      "release": "0.1.0",
      "archive": "releases/team.review-0.1.0.zip",
      "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
      "lane": "general",
      "blurb": "Review C# service changes",
      "labels": ["review", "dotnet"],
      "fit": {
        "fileTypes": [".cs", ".csproj"],
        "folders": ["services"],
        "commands": ["dotnet"],
        "dependencies": ["Example.Sdk"]
      }
    }
  ]
}
```

Replace the example checksum with the archive checksum written by `publish`, not the installed package content digest. Publication supplies `blurb` from the manifest summary; publishers can edit catalog labels and fit metadata before hosting it. Unknown fields, malformed values and duplicate release identities fail validation. Each fit family allows 24 literal values of at most 160 characters. File types are suffixes, folders are portable relative paths, commands are executable names, and dependencies are exact IDs. These fields contain no globs, regexes, shell commands or content-reading instructions.

```sh
code-assist plugins suggest engineering --seen-file src/Review.cs --seen-folder services/api --seen-command dotnet --seen-dependency Example.Sdk --json
```

Activity flags are repeatable. They supply facts explicitly: no file is opened, no manifest scanned and no command executed. After the explicit catalog read, matching runs locally and makes no additional network request. The operator receives no activity facts or match results. Installed IDs and policy-rejected IDs are excluded. Candidates rank by the number of matching signal families, then ID; reasons name only those families, not private path values. Matching is case-insensitive; folder matches include descendants but not similarly prefixed siblings. Search returns the newest release per ID within the lane and searches IDs, blurbs and labels.

Recommendations never download packages, approve content or install anything. They are an explicit CLI/runtime API, not automatic session tips or a VS Code recommendation panel. A recommendation is not proof that a package or its dependency closure will pass installation policy.

## Organization Policy

Place these controls under `policy.plugins` in managed settings. User files, project files, CLI settings overrides and ordinary environment overrides cannot loosen this managed-only section:

```json
{
  "policy": {
    "plugins": {
      "sources": ["https://plugins.example.com/assist-registry.json"],
      "ids": ["team.review", "team.base"],
      "reject": [],
      "required": {},
      "sequenceDigests": [],
      "localPackages": false
    }
  }
}
```

| Control | Contract |
| --- | --- |
| `sources` | Exact canonical registry URLs or absolute catalog/directory paths; `null` is unrestricted and `[]` blocks all registries. No user-editable aliases, host wildcards or workspace-relative paths |
| `ids` | Exact admitted plugin IDs; `null` is unrestricted and `[]` blocks all plugins |
| `reject` | Exact denied IDs; rejection wins over admission |
| `required` | Map of plugin ID to administrator-reviewed **content** SHA-256; requires already installed matching bytes and forces activation across user/workspace disable choices |
| `sequenceDigests` | Digests admitted to contribute hooks/workflows; `null` allows ordinary digest-approved sequences and `[]` rejects any package containing one |
| `localPackages` | Whether direct directory/ZIP and legacy installations without registry provenance may be admitted; default `true` |

`sources` governs registries, so combine it with `localPackages: false` to close the direct-install path. Canonical source identity is recorded with each installation and checked again at enable/load, not just before catalog access. A later source restriction blocks cached content too. Source aliases cannot rebind the administrator's literal source. Local directories and their parent directories must have appropriate administrator-controlled filesystem permissions; these checks do not protect an administrator-owned path that users can rewrite.

Add an installed package's digest from `plugins list --json` to `required` only after review. Required content does not download automatically or make a missing dependency available. Pin the dependency digests too, or explicitly approve them before the required root can load. Missing/mismatched requirements fail closed. Required IDs must also pass ID, source, sequence and `plugin.use` hard-policy rules. A managed requirement overrides user `plugins[].enabled: false` and prevents `disable`/`remove`, but never grants tool permissions or bypasses dispatcher authorization.

For offline CI or fleet images, provision verified registry archives and installation state through deployment tooling, with the normal state-directory setting. Reconstruct the store using normal installs, then distribute it with managed required digests. Nothing writes into a separate seed overlay, runs install helpers, or silently refreshes content. Organization policy is enforced by shared .NET composition for CLI and the server-backed VS Code text catalog; the editor has no independent JavaScript plugin runtime.

## Load Diagnostics

```sh
code-assist plugins diagnose --workspace --json
```

The report separates selected installation entries from readiness. Required pins, source/id/sequence restrictions, missing or tampered content, dependency failures, graph budgets and unreadable installation state have stable codes and repair actions. A blocking issue yields exit code `2`, the CLI's configuration-error code; otherwise exit code `0`. A damaged disabled package is reported without blocking the active set. Inspection never rewrites corrupt state, deletes content, downloads anything or changes approval.

| Code | Action |
| --- | --- |
| `state-unreadable` | Preserve `installed.json`, repair its schema or restore a known-good backup |
| `required-content` | Install the administrator-reviewed content digest |
| `policy-restricted` | Review managed ID, source and sequence restrictions; another local approval cannot override them |
| `content-unreadable` | Preserve suspect content and reinstall the verified release; do not approve tampered bytes to hide the failure |
| `dependency-unavailable` | Resolve dependency content/policy failures and enable the exact release in this scope |
| `graph-invalid` | Correct a dependency cycle or reduce the selected content graph |

Ordinary runtime loading remains fail-closed for an invalid selected graph. Disabled or installed does not mean loaded. Installation and policy changes affect subsequent catalog reads; there is no separate long-lived executable plugin worker to reload. MCP/LSP process lifecycle remains explicitly host-owned and inert in plugin definitions.

## Sequences and Services

A workflow entry can contain:

```json
{
  "timeoutSeconds": 30,
  "steps": [
    { "tool": "fs.read", "input": { "path": "README.md" } }
  ]
}
```

Its contribution must declare `"tools": ["fs.read"]`. Hooks use the same structure plus `"event": "tool.before"` or `"event": "tool.after"`. Sequences allow at most 32 steps and a 1-120 second deadline. Every step uses the existing authorized dispatcher and stops on a tool error. A failed pre-hook blocks the requested operation. A failed post-hook reports that the preceding operation remains committed; sequences do not provide rollback. Hook calls do not recursively trigger hooks through the decorator, and delegated calls retain their recorded ancestry tool restrictions.

Workflows currently have a runtime API, not an automatically registered model tool or standalone execution command. Plugin hooks/roles/skills integrate with composed tool services, not the current text-only HTTP session runner. General configured process hooks and the complete root coding loop remain separate pending work.

Service definitions accept `program`, argument arrays and environment variable **names**, never literal secret assignments. Language services additionally require mappings such as `"languages": { ".cs": "csharp" }`. These definitions are inert until a caller explicitly supplies an authorized lifecycle integration. Installing, enabling or listing one never starts its program.

## Restrictions and Measurement

User settings can restrict an already installed package, never install or approve one. Managed exact-digest requirements are the separate administrator-owned approval path:

```json
{ "plugins": [{ "package": "team.review", "enabled": false }] }
```

Equivalent transient overrides include `Plugins__0__Package` and `Plugins__0__Enabled`. A matching non-Allow `plugin.use` policy rule also blocks use; Ask fails closed rather than inventing an approval UI. `PluginLibraries__NAME__Source` addresses named library settings. Managed policy paths retain their managed-only trust even when addressed using double-underscore environment syntax. Plugin options remain reserved. Package content cannot edit settings or widen host/role permissions.

`plugins show` reports declared content and exact UTF-8 byte footprint. `plugins stats` stores local invocation counts and last-used timestamps, not prompts, replies, publisher analytics or remote telemetry. Text expansion counts as use, not successful task completion. Byte counts are not token estimates or billing claims. Behavioral evaluations, automated judges, automatic recommendation hints and publisher analytics are not delivered; manifest validation is not a behavioral evaluation.

The server exposes only authenticated `GET /v1/plugins/commands` and `POST /v1/plugins/expand`. They do not accept a workspace path and provide no install, enable, approval, removal or execution endpoint. Local cache objects may remain after removal; there is no cache garbage-collection command yet.