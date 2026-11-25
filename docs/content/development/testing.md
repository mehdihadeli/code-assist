---
title: "Testing"
description: "Owner-aligned unit/integration suites and opt-in live checks."
---

# Testing

Tests follow source ownership. Pure behavior belongs in unit suites; filesystem, process, composition, and CLI interactions belong in integration suites. Cross-project dependency rules live in the architecture suite.

## Run the solution

```sh
dotnet test --solution CodeAssist.slnx --no-progress --ignore-exit-code 8
```

The repository selects Microsoft Testing Platform. Code 8 means an all-skipped run; the flag permits opt-in live suites to remain disabled. It does not ignore failed tests.

Live test counts depend on configured models. With live tests disabled, the six provider probes and four skill-behavior scenarios each report one skipped case rather than listing every built-in adapter.

## Run a focused suite

```sh
dotnet run --project tests/CodeAssist.Configuration/UnitTests/CodeAssist.Configuration.UnitTests.csproj
```

Owner suites follow `tests/CodeAssist.<Project>/UnitTests` and/or `IntegrationTests`. Use a project's existing framework and helpers; do not add an empty suite just to mirror a directory.

Use Shouldly for assertions and name test methods `Should_ExpectedBehaviour_When_Condition`. Use WireMock.Net for mocked HTTP integration requests, with an ephemeral localhost server per test. Provider configuration fixtures exercise real SDK HTTP serialization, credential headers, aliases, body overrides and authentication failures; image fixtures check returned bytes and refusal to fetch URL-only responses. These fixtures do not use paid accounts. Existing filesystem, Git worktree, journal and MCP process fixtures exercise their real local components; the real-browser fixture has a separate `CODEASSIST_BROWSER_TESTS` opt-in.

## HTTP Server Checks

```sh
ENABLED_LIVE_TESTS=0 dotnet test --project tests/CodeAssist.Server/IntegrationTests/CodeAssist.Server.IntegrationTests.csproj --no-progress
```

Server tests start real Kestrel loopback listeners and run Microsoft Agent Framework sessions. Deterministic IChatClient fixtures cover committed history, session isolation, ordered SSE frames, failure rollback, unpublished reasoning limits, cancellation, concurrent capacity, shutdown, origin/Host protection and OpenAPI bearer requirements. Additional WireMock.Net fixtures exercise the complete HTTP-to-framework-to-official-OpenAI/Anthropic-SDK streaming path, including aliases, credential headers and output limits. No paid calls are made. CLI server checks cover command registration, startup guards and environment-secret redaction.

These are local integration tests, not evidence that every provider account, network or deployment environment works. The [server guide](/guide/server/) states which runtime services remain pending.

## CLI Conversation Checks

```sh
ENABLED_LIVE_TESTS=0 dotnet test --project tests/CodeAssist.Cli/IntegrationTests/CodeAssist.Cli.IntegrationTests.csproj --no-progress
```

Conversation checks run the actual Spectre command parser and DI container with injected input and a captured console. WireMock.Net service fixtures cover authenticated attachment, input limits, literal text, JSON-line events, failed/truncated streams, chat history and session controls. Managed-mode checks launch the bundled standalone service and stream a local WireMock.Net model response through the official SDK and Agent Framework. No paid calls are made. Full-screen terminal interaction, timing-based Ctrl+C behavior and live-account compatibility are not established by these fixtures.

## VS Code Extension Checks

From the extension folder:

```sh
cd vscode-extension
npm ci
npm test
npm run test:host
npm run package
```

Node tests cover authenticated loopback transport, bounded UTF-8 SSE, cancellation, failure/no-retry behavior, process ownership, user/environment settings, mocked native APIs, workspace trust, explicit selection and conversation isolation. `CODE_ASSIST_TEST_SERVER` optionally points to an absolute built server DLL path for a real .NET connect/list/shutdown smoke with no inference. `VSCODE_EXECUTABLE_PATH` selects an installed editor for the isolated host smoke; otherwise the editor-test tooling downloads a test copy.

The real editor-host smoke verifies activation, native chat registration, commands, settings and disconnect. It uses a temporary profile with other installed extensions disabled and never changes the user's normal profile. It does not verify paid/native-chat accounts, full chat UI rendering, remote-workspace placement or end-to-end workspace editing. The [editor guide](/guide/vscode/) documents current limits.

## Live provider checks

Live cases use the normal settings loader and production provider composition. Configure providers in user `settings.json`/`settings.jsonc` or through the same environment names the application accepts. Project settings still cannot introduce providers. `CODE_ASSIST_HOME` selects a different user settings directory.

The only test-specific configuration is `ENABLED_LIVE_TESTS=1` (or `true`). Missing, empty, `0`, and other values disable live requests. The integration harness reads `.env` beside `CodeAssist.slnx`; process environment overrides its values, including an explicit opt-out. It does not write settings files or export credentials into the process environment.

For the DeepSeek OpenAI-compatible route, a local `.env` can contain:

```dotenv
ENABLED_LIVE_TESTS=0
Agent__Model=openai-byok/deepseek-flash
Providers__default__Enabled=false
Providers__openai-byok__Protocol=openai
Providers__openai-byok__Api=chat
Providers__openai-byok__Endpoint=https://api.deepseek.com
Providers__openai-byok__ApiKey={env:DEEPSEEK_API_KEY}
Providers__openai-byok__MaxOutputTokens=256
Providers__openai-byok__Models__deepseek-flash__Tools=true
Providers__openai-byok__Models__deepseek-flash__Input__0=text
Providers__openai-byok__Models__deepseek-flash__Output__0=text
Providers__openai-byok__Models__deepseek-flash__Body__thinking='{"type":"disabled"}'
DEEPSEEK_API_KEY=
```

Provide the key locally, then enable the switch when ready to incur provider charges. The dotenv parser accepts hyphenated provider/model keys and quoted JSON values without shell expansion. The test harness reads the file directly; do not source these keys as shell assignments.

The `apiKey` reference names a variable instead of duplicating its credential. A stale process `DEEPSEEK_API_KEY` overrides the dotenv key; remove it from the test child's environment with `env -u DEEPSEEK_API_KEY` in Git Bash when testing the file's credential. DeepSeek thinking mode rejects required tool choices, so this example disables thinking through normal model body settings rather than changing test behavior.

The equivalent non-secret user settings are:

```json
{
	"agent": { "model": "openai-byok/deepseek-flash" },
	"providers": {
		"default": { "enabled": false },
		"openai-byok": {
			"protocol": "openai",
			"api": "chat",
			"endpoint": "https://api.deepseek.com",
			"apiKey": "{env:DEEPSEEK_API_KEY}",
			"maxOutputTokens": 256,
			"models": {
				"deepseek-flash": {
					"tools": true,
					"input": ["text"],
					"output": ["text"],
					"body": { "thinking": "{\"type\":\"disabled\"}" }
				}
			}
		}
	}
}
```

Keep `ENABLED_LIVE_TESTS` and the credential in the environment or ignored local `.env`. A direct `Providers__openai-byok__ApiKey` also works. Literal API keys in settings files remain rejected; exact references are accepted only in trusted user/managed provider settings. Environment overlays, including nested model capabilities, headers, options, lists and whole-section JSON, follow normal validation, merge behavior and managed locks.

The suite tests each enabled, explicitly configured chat model that can output text. Disabled providers/models and image/embedding models are excluded. When a provider has no explicit model map, `agent.model` can select its model. Tool-call checks use the model's normal `tools` capability. There are no separate test model, endpoint, credential, provider-filter or tool-capability variables. Missing credentials for a selected remote provider fail visibly; local key-optional adapters remain supported.

Six scenarios cover text responses, streamed text, tool requests, conversation history, streamed tool arguments and a complete tool-result round trip. The round-trip check invokes a local function and returns its fresh verification code through a tool-result message before asking the provider to report it. Tool scenarios skip models whose normal `tools` capability is false. Paid image generation is not exercised by these chat probes.

```sh
dotnet test --project tests/CodeAssist.Models/IntegrationTests/CodeAssist.Models.IntegrationTests.csproj --filter-class '*LiveProviderTests' --no-progress --ignore-exit-code 8
```

To force requests off in Git Bash, prefix the command with `ENABLED_LIVE_TESTS=0`. In PowerShell, set `$env:ENABLED_LIVE_TESTS='0'` before running it. Do not enable live tests merely to validate documentation or deterministic routing. Native cloud identity providers still require their SDK credential setup; dotenv entries are not exported as SDK process credentials.

Passing adapter tests does not establish real-account access, provider pricing, or production readiness. An all-skipped live suite is not evidence that a provider request succeeded.

## Skill Behavior Evaluations

Deterministic server tests exercise framework-driven guidance loading, resource continuation, unrelated requests, human-only denial, per-turn read/output budgets, hidden-tool history accounting and rollback. WireMock.Net checks verify that role restrictions, disabled tools, managed deny/ask statements and models with `tools: false` suppress conversational skill functions. These fixtures verify infrastructure, not model judgment.

The opt-in skill evaluation suite uses the same normal provider settings, credentials and `ENABLED_LIVE_TESTS` switch as provider probes. It tests every enabled tool-capable text model against four synthetic scenarios: relevant activation in isolation, coexistence with a competing skill, an unrelated arithmetic request and a near-miss that mentions the domain without requesting its workflow. The isolated scenario also runs without skills to establish a baseline.

Positive cases require loading the intended guidance, reading a bundled resource and returning its fresh verification code in an exact prescribed format. Negative cases require the expected answer without loading guidance or resources. Each case emits a JSON rubric record with model identity, scenario, output-match result, loaded identities, resource accesses, baseline match where applicable and elapsed time. The suite does not print credentials or require a second provider configuration.

```sh
dotnet test --project tests/CodeAssist.Models/IntegrationTests/CodeAssist.Models.IntegrationTests.csproj --filter-class '*LiveSkillEvaluationTests' --no-progress --ignore-exit-code 8
```

An enabled run makes paid model requests: at least five initial turns per configured tool-capable model, plus tool-loop continuation requests. Keep live requests disabled for ordinary infrastructure checks. Discovery with four skipped cases proves only the opt-in boundary, not activation accuracy or output quality. These representative evaluations are not a score for every user skill, an automated reviewer or an enterprise deployment gate.

## Documentation checks

```sh
npm --prefix docs test
npm --prefix docs run build
```

Content tests verify that authored Markdown matches the published page catalog, internal links target published routes, JSON examples parse, and build hooks cannot overwrite content. They also cover base-path handling and reject stale pages or `.mdx` content files. The production build compiles Markdown through Nextra, checks TypeScript, exports the static site, and builds its Pagefind full-text index.

Check mobile navigation, search results, copy controls, long command blocks, and previous/next links in a browser after changing the shell. See [contributing](/development/contributing/) for the change workflow.
