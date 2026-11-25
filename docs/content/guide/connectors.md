---
title: "Connectors"
description: "Configure session-owned tool servers, authorization, prompts and provider-hosted execution."
---

# Connectors

CodeAssist supports two separate execution paths. **Local connections** use the official .NET MCP SDK: CodeAssist starts a trusted process or connects to an HTTP endpoint. **Provider-hosted connections** ask a configured model provider to contact a public server and execute its tools. The second path does not pass through the local tool dispatcher or its per-call approvals.

Neither path makes server instructions, descriptions, prompts or results trusted. The [coverage table](#capability-coverage) distinguishes implemented behavior from remaining work.

## Inspect definitions

```sh
code-assist connectors list
code-assist connectors show docs
code-assist connectors probe docs
```

`list` and `show` inspect configuration only. `probe` connects, initializes the protocol and lists admitted tools without executing them. It can start a configured process or request authorization. Connections opened by an individual CLI command close when it exits.

## Define an HTTP connector

```sh
code-assist connectors add docs --url https://mcp.example.com/
code-assist connectors show docs
```

The URL is a placeholder. Replace it with a server you control or trust. Adding it does not connect to that URL.

## Define a process connector

A user settings file can contain a stdio definition:

```json
{
  "connectors": {
    "servers": {
      "local-tools": {
        "transport": "stdio",
        "command": "dotnet",
        "arguments": ["C:/tools/my-mcp-server/server.dll"]
      }
    }
  }
}
```

The assembly path is illustrative. Explicit connection starts that process; CodeAssist does not install it or isolate it in an OS sandbox. The default child environment is curated rather than inherited wholesale. Project settings cannot introduce connector processes; put trusted definitions in user or managed settings.

## HTTP Options and Filters

An HTTP definition can use headers, an explicit transport mode and tool admission rules:

```json
{
  "connectors": {
    "servers": {
      "inventory": {
        "transport": "http",
        "url": "https://inventory.example.com/mcp",
        "httpMode": "streaming",
        "headers": { "Authorization": "{env:INVENTORY_AUTHORIZATION}" },
        "freezeCatalog": false,
        "tools": {
          "defaultEnabled": false,
          "entries": { "lookup": true },
          "deny": ["delete*"]
        }
      }
    }
  }
}
```

Set `INVENTORY_AUTHORIZATION` outside settings to the complete header value, including its authentication scheme. Environment references must occupy the complete value; missing, partial and recursive references are rejected. Child `environment` values support the same references. Do not commit credentials.

`httpMode` accepts `auto`, `streaming` or `events`, selecting automatic negotiation, Streamable HTTP or the older SSE transport. HTTPS is required except on loopback. URLs cannot contain credentials, query strings or fragments. Private-address access requires `connectors.allowPrivateHosts`; enabling it does not disable certificate validation. Configured paths are retained.

Global startup/catalog/call timeouts default to 30/30/120 seconds. Per-server `startupTimeoutSeconds`, `catalogTimeoutSeconds` and `callTimeoutSeconds` override them. `protocolRevision` optionally overrides SDK protocol negotiation. `freezeCatalog` retains the initial tool snapshot; otherwise discovery refreshes explicitly and on list-change notifications.

Tool overrides and the default-enabled setting are applied before optional allow patterns; deny patterns always win. Managed `connector.use` restrictions can exclude an entire configured server. Remote descriptions and read-only annotations do not confer execution permission.

Every setting also accepts its equivalent transient environment path, including dictionary keys and list indices. Examples: `Connectors__Servers__inventory__HttpMode`, `Connectors__Servers__inventory__Headers__Authorization`, and `Connectors__Servers__inventory__Tools__Deny__0`. These overrides never rewrite saved settings.

## OAuth and Local Credentials

```sh
code-assist connectors authorize inventory
code-assist connectors logout inventory
```

Authorization initializes the connection and uses the SDK's discovery, PKCE, state verification and refresh support when the endpoint challenges. A trusted interactive host owns browser launch and callback handling; there is no automatic console authorization fallback. Run interactive CLI authorization before using a managed editor server that cannot launch its own browser.

Optional `oauth` settings include `enabled`, `clientId`, `clientSecret`, `scope`, `authority`, `clientMetadataUri`, `redirectUri` and `callbackPort`. Client secrets require complete environment references. A configured authority must match an advertised authorization server; it does not override discovery with an arbitrary token endpoint. The built-in browser callback uses a loopback listener. Other redirect mechanisms require a host-supplied state-aware callback.

On Windows, the CLI stores SDK tokens in its state directory using current-user OS encryption. Cache identities include workspace, server, endpoint, headers and client configuration. Tokens are not written into project settings. Other platforms have no default persistent cache; SDK hosts can supply a secure token-cache implementation.

`logout` removes the locally owned cache entry. It does not revoke tokens at their issuer, sign out a browser or invalidate credentials already held by other running sessions. Disconnect those sessions or stop their host when removing access. Removing a connector definition alone does not delete its credential cache.

## Resources, Prompts and Interaction

```sh
code-assist connectors prompts inventory
code-assist connectors prompts inventory inspect --arg target=service
```

Prompt discovery and retrieval are bounded and validate declared arguments. Retrieval returns user/assistant messages without calling a model, executing a tool or promoting server text to system instructions. Unsupported roles are rejected.

SDK hosts can expose resource listing, templates and reads through the shared resource ports. Mixed results preserve bounded text, structured JSON, embedded resources, links and media; declared output schemas use offline validation. Large tool text can use session-owned retained-output handles.

Trusted SDK hosts can provide bounded form elicitation through `IUserInteraction`. Form answers are validated against the declared schema. URL elicitation requires a trusted browser callback, an HTTPS link and explicit user consent. Passwords and credentials must not be entered into these forms. The native editor does not yet provide an elicitation/approval bridge.

## Native Editor and HTTP Management

In a trusted workspace, **CodeAssist: Manage Connectors** selects a configured server and explicitly connects, disconnects or forgets credentials. Startup and credential removal require confirmation. **CodeAssist: Inspect Connector Prompt** retrieves a prompt from an already connected server and opens its JSON for inspection. It captures no editor source and does not submit the prompt for inference.

The authenticated [HTTP service](/guide/server/) exposes these session-owned routes:

| Method | Suffix under `/v1/sessions/{id}/connectors` | Behavior |
| --- | --- | --- |
| GET | `/` | Inspect configured servers without connecting |
| GET | `/{server}/connection` | Inspect connection status |
| POST | `/{server}/connection` | Connect with body `{"confirmed":true}` |
| DELETE | `/{server}/connection` | Disconnect without deleting credentials |
| DELETE | `/{server}/credentials` | Disconnect and forget locally owned credentials |
| GET | `/{server}/prompts` | List prompt metadata |
| POST | `/{server}/prompts/resolve` | Retrieve with `name` and optional `arguments` |

Mutations require an idle existing session; deleting a session releases its connector handles. There is no unrestricted HTTP tool-call endpoint. Managing a local connection does not automatically make its tools available in ordinary editor conversations. SDK hosts must wire the remote-tool gateway through an authorized dispatcher.

## Provider-Hosted Execution

For a configured Messages-protocol provider, enable its separate `remoteTools` settings only after accepting the remote-execution boundary:

```json
{
  "providers": {
    "production": {
      "protocol": "anthropic",
      "remoteTools": {
        "enabled": true,
        "acceptRemoteExecution": true,
        "servers": {
          "inventory": {
            "url": "https://inventory.example.com/mcp",
            "token": "{env:INVENTORY_ACCESS_TOKEN}",
            "defaultEnabled": false,
            "deferred": true,
            "toolRules": {
              "lookup": { "enabled": true, "deferred": false },
              "delete": { "enabled": false }
            },
            "cacheSeconds": 300
          }
        }
      }
    }
  }
}
```

Use the normal provider credentials and model route. The token is an already acquired access token, supplied by a complete environment reference; this path does not acquire or refresh it. The provider needs a publicly reachable HTTPS endpoint. It cannot reach a local stdio process merely because that process is configured locally.

Shared session composition also checks role visibility, disabled-tool settings and managed `connector.hosted`/`tool.use` restrictions. Direct SDK composition requires an explicit admission callback. These checks admit the remote execution path; they are not local per-call execution approvals.

Global/per-tool enabling, deferred discovery and tool-search insertion use structured request shaping without dropping local function tools. `cacheSeconds` accepts `0`, `300` or `3600`. Optional `fixedTools` pins names, descriptions and offline-validated schema strings; the adapter selects the corresponding native protocol headers. Actual account/model eligibility still depends on the provider.

Hosted call/result blocks are preserved in framework history and streamed as informational activity. The native client records them in **CodeAssist Connector Activity**, not as local execution requests. These records may contain sensitive server data. Retention and deletion follow the external services' policies; this connector path is not a zero-data-retention guarantee.

## Existing Tunnels

A tunnel that exposes a normal reachable HTTPS MCP endpoint can be configured as a URL. Header authentication and normal certificate validation still apply. Merely accepting such a URL does not provision, secure or operate the tunnel.

CodeAssist does not create outbound relays, routing proxies, organization/workspace tunnel objects or preview entitlements. It does not register customer CAs, configure mutual TLS, rotate tunnel tokens/certificates or provide deployment packages. Provision those controls with the infrastructure owner before supplying an endpoint. Custom CA/client-certificate settings and workload-federated tunnel credentials are not implemented.

## Disable without removing

```sh
code-assist connectors disable docs
code-assist connectors enable docs
code-assist connectors remove docs
```

Disabling retains the definition. Removing deletes its settings entry. Neither operation controls an already running external process.

## Trust Boundary

Treat connector processes as code execution. Review executable paths, arguments, inherited environment and remote endpoints before supplying a transport to the SDK. Keep secrets outside committed definitions. Configuration validation and local diagnostics are not MCP authentication or isolation guarantees.

Imported tool lists refresh atomically; malformed refreshes leave the previous snapshot intact. Existing catalogs keep their advertised executor snapshot until the host rebuilds them. The `mcp.remote` gateway validates admitted tool input through the local dispatcher path. Model-visible content cannot authorize connection startup or widen its role.

Interrupted or failed operations are not automatically replayed. Cancellation cannot undo an external side effect. Keep TLS validation enabled and verify both the endpoint owner and each process executable before connecting.

## Capability Coverage

**Implemented** indicates executable behavior with focused checks. **Partial** indicates a narrower implementation. **Not implemented** does not imply a feature can be enabled by instructions alone.

| Capability | Status | Boundary |
| --- | --- | --- |
| Local/remote configured connections | Implemented | Trusted stdio, HTTP auto/streaming/events modes; HTTP fixture coverage, not transport-conformance certification |
| Enable/disable, env references, headers, filters, deadlines | Implemented | Settings/CLI, per-session lifecycle and explicit startup; no secret persistence in settings |
| Dynamic catalogs and frozen snapshots | Implemented | Explicit/list-change refresh, bounded metadata and atomic replacement |
| SDK OAuth and interactive callback | Implemented | Discovery, PKCE/state, scopes/client metadata/registration and SDK refresh; trusted host callback required |
| Persistent local tokens | Partial | Current-user encrypted Windows cache; other OS backends, issuer revocation and headless pasted-code UI absent |
| Resources and prompts | Implemented | Bounded SDK ports, CLI prompts and native inspection; no automatic instruction elevation |
| Elicitation | Partial | Validated JSON forms and consented URLs through trusted host callbacks; rich native widgets absent |
| HTTP/native connector management | Implemented | Explicit consent, status/disconnect/forget/prompt routes; session deletion releases handles |
| Automatic local MCP use in editor chat | Not implemented | Authorized SDK gateway exists, but ordinary editor chat does not register these local tools |
| Startup instructions/correlation and eager startup | Not implemented | No automatic initialization-instruction injection, call-correlation UI or `eagerStart` behavior |
| Read-only trust optimization | Not implemented | `trust`/`readOnlyTools` do not reduce the conservative execution permission |
| Hosted toolsets and filtering | Implemented | Explicit path admission, mixed local tools, per-tool enable/defer controls and native search insertion |
| Hosted response/history streaming | Implemented | Bounded call/result/listing preservation and input-delta assembly; fixture-tested, no paid account verification |
| Hosted caching/pinned schemas | Partial | Structured settings and wire support; provider-specific cache effectiveness and account eligibility not measured |
| Hosted token acquisition/refresh | Not implemented | Caller supplies an existing access token |
| Hosted batch execution and dynamic server addition | Not implemented | No batch API or mid-conversation connector-control surface |
| Tunnel endpoint compatibility | Partial | Existing ordinary HTTPS endpoints only |
| Tunnel provisioning, ownership and routing control plane | Not implemented | No relay/proxy deployment, hostname/IP allowlist management, console/API provisioning or preview entitlement handling |
| Tunnel identity and certificate lifecycle | Not implemented | No customer CA registration, inner/outer TLS orchestration, mTLS, OIDC federation, token rotation or certificate renewal |
| Tunnel deployment artifacts | Not implemented | No Compose/Helm packages or infrastructure lifecycle commands |

Deterministic tests require no live cloud tunnel, OAuth issuer account or paid model call. You still need to verify authentication, connectivity and model support with your configured services before relying on them.

See [configuration](/guide/configuration/) for trust tiers and [feature status](/guide/features/) for runtime progress.
