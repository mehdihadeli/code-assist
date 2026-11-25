import type { components } from "codeassist-contracts";

export type Session = Pick<components["schemas"]["AgentSessionDto"], "id" | "model" | "running"> & { messageCount: number };

export interface PluginCommand {
  name: string;
  kind: string;
  description: string;
  descriptionBytes: number;
  contentBytes: number;
}

export interface SkillCommand {
  id: string;
  description: string;
  origin: string;
  title: string | undefined;
  hint: string | undefined;
  parameters: string[];
}

const skillId = /^[a-zA-Z0-9._-]+(?:\/[a-zA-Z0-9._-]+)?$/;
const skillParameter = /^[a-zA-Z][a-zA-Z0-9_]{0,31}$/;

export class ClientError extends Error {}

export interface ConnectorStatus { server: string; connected: boolean; tools: number }
export interface ConnectorPrompt { name: string; description: string | undefined; arguments: { name: string; required: boolean }[] }
const connectorName = /^[a-zA-Z0-9_-]{1,80}$/;

function connectorStatus(value: unknown): ConnectorStatus {
  const item = record(value);
  if (typeof item.server !== "string" || !connectorName.test(item.server) || typeof item.connected !== "boolean" ||
      !Number.isSafeInteger(item.tools) || (item.tools as number) < 0 || (item.tools as number) > 1000) throw new ClientError("Invalid connector state.");
  return { server: item.server, connected: item.connected, tools: item.tools as number };
}

const identifier = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const pluginName = /^[a-z][a-z0-9-]{0,31}(?:\.[a-z][a-z0-9-]{0,31}){1,3}\/[a-z][a-z0-9-]{0,47}$/;

export function serverAddress(value: string): string {
  let url: URL;
  try { url = new URL(value); } catch { throw new ClientError("Invalid server address."); }
  if (!(url.protocol === "http:" || url.protocol === "https:") ||
      !["127.0.0.1", "[::1]", "localhost"].includes(url.hostname) ||
      url.username || url.password || url.search || url.hash || url.pathname !== "/")
    throw new ClientError("Use a loopback server origin without paths, credentials, query or fragment.");
  return url.origin;
}

function record(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new ClientError("Invalid server response.");
  return value as Record<string, unknown>;
}

function session(value: unknown): Session {
  const item = record(value);
  if (typeof item.id !== "string" || !identifier.test(item.id) || typeof item.model !== "string" ||
      typeof item.running !== "boolean" || !Number.isSafeInteger(item.messageCount) || (item.messageCount as number) < 0)
    throw new ClientError("Invalid session response.");
  return { id: item.id, model: item.model, running: item.running, messageCount: item.messageCount as number };
}

export class CodeAssistClient {
  readonly endpoint: string;
  private readonly closed = new AbortController();

  constructor(endpoint: string, private readonly token: string) {
    this.endpoint = serverAddress(endpoint);
    if (token.length < 32 || token.length > 512 || /[\r\n]/.test(token)) throw new ClientError("Invalid server token.");
  }

  dispose(): void { this.closed.abort(); }

  async check(signal?: AbortSignal): Promise<void> {
    const health = record(await this.json("/v1/health", "GET", undefined, signal));
    if (health.application !== "code-assist" || health.protocol !== "1" || health.status !== "ready")
      throw new ClientError("Server identity or protocol is incompatible.");
  }

  async create(model?: string, signal?: AbortSignal): Promise<Session> {
    return session(await this.json("/v1/sessions", "POST", model ? { model } : {}, signal));
  }

  async sessions(signal?: AbortSignal): Promise<Session[]> {
    const items: unknown = await this.json("/v1/sessions", "GET", undefined, signal);
    if (!Array.isArray(items) || items.length > 1024) throw new ClientError("Invalid session list.");
    return items.map(session);
  }

  async cancel(id: string): Promise<void> {
    await this.json(this.path(id) + "/cancel", "POST");
  }

  async connectors(id: string, signal?: AbortSignal): Promise<ConnectorStatus[]> {
    const items: unknown = await this.json(this.path(id) + "/connectors", "GET", undefined, signal);
    if (!Array.isArray(items) || items.length > 64) throw new ClientError("Invalid connector list.");
    const states = items.map(connectorStatus);
    if (new Set(states.map(item => item.server)).size !== states.length) throw new ClientError("Duplicate connector identity.");
    return states;
  }

  async connectorChange(id: string, server: string, action: "connect" | "disconnect" | "logout", signal?: AbortSignal): Promise<void> {
    const path = this.connectorPath(id, server) + (action === "logout" ? "/credentials" : "/connection");
    if (action === "connect") {
      const state = connectorStatus(await this.json(path, "POST", { confirmed: true }, signal));
      if (state.server !== server || !state.connected) throw new ClientError("Connector did not reach connected state.");
    } else {
      const response = await this.request(path, "DELETE", undefined, signal);
      await response.body?.cancel();
      if (response.status !== 204) throw new ClientError("Unexpected connector deletion response.");
    }
  }

  async connectorPrompts(id: string, server: string, signal?: AbortSignal): Promise<ConnectorPrompt[]> {
    const items: unknown = await this.json(this.connectorPath(id, server) + "/prompts", "GET", undefined, signal);
    if (!Array.isArray(items) || items.length > 256) throw new ClientError("Invalid connector prompt list.");
    const name = (value: unknown): value is string => typeof value === "string" && value.length > 0 && value.length <= 128 && !/[\u0000-\u001f]/.test(value);
    const prompts = items.map(value => {
      const item = record(value);
      if (!name(item.name) || item.description != null && (typeof item.description !== "string" || item.description.length > 8000) || !Array.isArray(item.arguments) || item.arguments.length > 32)
        throw new ClientError("Invalid connector prompt metadata.");
      const args = item.arguments.map(value => {
        const parameter = record(value);
        if (!name(parameter.name) || typeof parameter.required !== "boolean") throw new ClientError("Invalid connector prompt argument.");
        return { name: parameter.name, required: parameter.required };
      });
      if (new Set(args.map(item => item.name)).size !== args.length) throw new ClientError("Duplicate connector prompt argument.");
      return { name: item.name, description: item.description as string | undefined, arguments: args };
    });
    if (new Set(prompts.map(item => item.name)).size !== prompts.length) throw new ClientError("Duplicate connector prompt identity.");
    return prompts;
  }

  async connectorPrompt(id: string, server: string, name: string, args: Record<string, string>, signal?: AbortSignal): Promise<string> {
    if (!name.trim() || name.length > 128 || Object.keys(args).length > 32 || Object.entries(args).some(([key, value]) => key.length > 128 || typeof value !== "string" || value.length > 8000))
      throw new ClientError("Invalid connector prompt invocation.");
    const result = record(await this.json(this.connectorPath(id, server) + "/prompts/resolve", "POST", { name, arguments: args }, signal));
    if (!Array.isArray(result.messages) || result.messages.length > 64 || result.messages.some(value => !["user", "assistant"].includes(record(value).role as string)))
      throw new ClientError("Invalid connector prompt roles.");
    const text = JSON.stringify(result, null, 2);
    if (Buffer.byteLength(text, "utf8") > 262_144) throw new ClientError("Connector prompt exceeds its preview budget.");
    return text;
  }

  private connectorPath(id: string, server: string): string {
    if (!connectorName.test(server)) throw new ClientError("Invalid connector identity.");
    return this.path(id) + `/connectors/${encodeURIComponent(server)}`;
  }

  async plugins(signal?: AbortSignal): Promise<PluginCommand[]> {
    const items: unknown = await this.json("/v1/plugins/commands", "GET", undefined, signal);
    if (!Array.isArray(items) || items.length > 1024) throw new ClientError("Invalid plugin command list.");
    const names = new Set<string>();
    return items.map(value => {
      const item = record(value);
      if (typeof item.name !== "string" || item.name.length > 177 || !pluginName.test(item.name) || names.has(item.name) ||
          typeof item.kind !== "string" || !["skill", "prompt", "role", "style"].includes(item.kind) ||
          typeof item.description !== "string" || item.description.length > 1024 ||
          !Number.isSafeInteger(item.descriptionBytes) || (item.descriptionBytes as number) < 0 || (item.descriptionBytes as number) > 8192 ||
          !Number.isSafeInteger(item.contentBytes) || (item.contentBytes as number) < 1 || (item.contentBytes as number) > 1_048_576)
        throw new ClientError("Invalid plugin command metadata.");
      names.add(item.name);
      return { name: item.name, kind: item.kind, description: item.description, descriptionBytes: item.descriptionBytes as number, contentBytes: item.contentBytes as number };
    });
  }

  async pluginPrompt(name: string, input: string, signal?: AbortSignal): Promise<string> {
    if (name.length > 177 || !pluginName.test(name) || input.length > 65_536) throw new ClientError("Invalid plugin selection or input.");
    const response = record(await this.json("/v1/plugins/expand", "POST", { name, input }, signal));
    if (typeof response.text !== "string" || !response.text.trim() || response.text.length > 65_536)
      throw new ClientError("Plugin guidance exceeds the conversation input limit or is invalid.");
    return response.text;
  }

  async skills(signal?: AbortSignal): Promise<SkillCommand[]> {
    const items: unknown = await this.json("/v1/skills/commands", "GET", undefined, signal);
    if (!Array.isArray(items) || items.length > 1024) throw new ClientError("Invalid skill command list.");
    const names = new Set<string>();
    return items.map(value => {
      const item = record(value);
      if (typeof item.id !== "string" || item.id.length > 240 || !skillId.test(item.id) || item.id.split("/").some(part => part === "." || part === "..") || names.has(item.id) ||
          typeof item.description !== "string" || item.description.length > 1024 || /[\u0000-\u0008\u000b\u000c\u000e-\u001f]/.test(item.description) ||
          typeof item.origin !== "string" || item.origin.length > 128 || !/^[a-zA-Z0-9._-]+$/.test(item.origin) ||
          (item.title != null && (typeof item.title !== "string" || item.title.length > 1024)) ||
          (item.hint != null && (typeof item.hint !== "string" || item.hint.length > 1024)) ||
          !Array.isArray(item.parameters) || item.parameters.length > 16 || item.parameters.some(name => typeof name !== "string" || !skillParameter.test(name)) ||
          new Set(item.parameters).size !== item.parameters.length)
        throw new ClientError("Invalid skill command metadata.");
      names.add(item.id);
      return { id: item.id, description: item.description, origin: item.origin, title: item.title as string | undefined, hint: item.hint as string | undefined, parameters: item.parameters as string[] };
    });
  }

  async skillPrompt(id: string, input: string, args: string[] = [], signal?: AbortSignal): Promise<string> {
    if (id.length > 240 || !skillId.test(id) || id.split("/").some(part => part === "." || part === "..") || input.length > 4000 || args.length > 16 || args.some(value => value.length > 4000))
      throw new ClientError("Invalid skill selection or invocation values.");
    const response = record(await this.json("/v1/skills/expand", "POST", { id, input, arguments: args }, signal));
    if (typeof response.text !== "string" || !response.text.trim() || Buffer.byteLength(response.text, "utf8") > 65_536)
      throw new ClientError("Skill guidance exceeds the conversation input limit or is invalid.");
    return response.text;
  }

  async messages(id: string): Promise<string> {
    const items: unknown = await this.json(this.path(id) + "/messages", "GET");
    if (!Array.isArray(items) || items.length > 1024) throw new ClientError("Invalid message history.");
    return items.map(value => {
      const item = record(value);
      if ((item.role !== "user" && item.role !== "assistant") || typeof item.text !== "string")
        throw new ClientError("Invalid message history.");
      return `[${item.role}]\n${item.text}`;
    }).join("\n\n");
  }

  async stream(id: string, text: string, delta: (text: string) => void, signal?: AbortSignal,
    activity?: (kind: string, content: Record<string, unknown>) => void): Promise<void> {
    if (!text.trim() || text.length > 65_536) throw new ClientError("Message must contain 1 to 65,536 characters.");
    const response = await this.request(this.path(id) + "/messages/stream", "POST", { text }, signal);
    if (!response.headers.get("content-type")?.startsWith("text/event-stream") || !response.body) {
      await response.body?.cancel();
      throw new ClientError("Server did not return an event stream.");
    }
    const reader = response.body.getReader();
    const decoder = new TextDecoder("utf-8", { fatal: true });
    let pending = "";
    let sequence = 0;
    let turn: string | undefined;
    let transferred = 0;
    let generated = 0;
    try {
      while (true) {
        const chunk = await reader.read();
        if (chunk.done) throw new ClientError("Stream interrupted. Read committed history before retrying; the prompt was not resent.");
        transferred += chunk.value.byteLength;
        if (transferred > 16_777_216) throw new ClientError("Stream exceeds the transfer limit.");
        pending += decoder.decode(chunk.value, { stream: true });
        let boundary: RegExpExecArray | null;
        while ((boundary = /\r?\n\r?\n/.exec(pending)) !== null) {
          const frame = pending.slice(0, boundary.index);
          pending = pending.slice(boundary.index + boundary[0].length);
          if (frame.length > 1_048_576) throw new ClientError("Stream event exceeds its limit.");
          const lines = frame.split(/\r?\n/);
          const data = lines.filter(line => line.startsWith("data:")).map(line => line.slice(5).trimStart()).join("\n");
          if (!data) continue;
          const item = record(JSON.parse(data) as unknown);
          if (typeof item.turnId !== "string" || !identifier.test(item.turnId) || item.sequence !== sequence + 1 || sequence >= 100_000 ||
              (turn !== undefined && turn !== item.turnId) || (sequence === 0 && item.kind !== "turn.started"))
            throw new ClientError("Invalid or out-of-order stream event.");
          turn = item.turnId;
          sequence++;
          if (item.kind === "text.delta") {
            if (typeof item.text !== "string") throw new ClientError("Invalid text event.");
            generated += item.text.length;
            if (generated > 262_144) throw new ClientError("Response exceeds the text limit.");
            delta(item.text);
          } else if (["hosted.call", "hosted.result", "hosted.listing", "hosted.activity"].includes(item.kind as string)) {
            if (typeof item.text !== "string" || item.text.length > 262_144) throw new ClientError("Invalid hosted activity event.");
            generated += item.text.length;
            if (generated > 262_144) throw new ClientError("Response exceeds the activity limit.");
            const content = record(JSON.parse(item.text) as unknown);
            activity?.(item.kind as string, content);
          } else if (item.kind === "turn.completed") return;
          else if (item.kind === "turn.failed" || item.kind === "turn.canceled")
            throw new ClientError("Turn did not complete. Inspect committed history before retrying.");
          else if (item.kind !== "turn.started" || sequence !== 1) throw new ClientError("Unsupported stream event.");
        }
        if (pending.length > 1_048_576) throw new ClientError("Stream event exceeds its limit.");
      }
    } catch (error) {
      if (error instanceof ClientError) throw error;
      throw new ClientError(signal?.aborted ? "Request canceled." : "Stream interrupted. Inspect committed history before retrying.");
    } finally {
      await reader.cancel().catch(() => undefined);
      reader.releaseLock();
    }
  }

  private path(id: string): string {
    if (!identifier.test(id)) throw new ClientError("Invalid session ID.");
    return `/v1/sessions/${id}`;
  }

  private async json(path: string, method: string, body?: unknown, signal?: AbortSignal): Promise<unknown> {
    const response = await this.request(path, method, body, signal);
    if (!response.body) throw new ClientError("Empty server response.");
    const reader = response.body.getReader();
    const decoder = new TextDecoder("utf-8", { fatal: true });
    let bytes = 0;
    let content = "";
    try {
      while (true) {
        const chunk = await reader.read();
        if (chunk.done) break;
        bytes += chunk.value.byteLength;
        if (bytes > 4_194_304) throw new ClientError("Server response exceeds its limit.");
        content += decoder.decode(chunk.value, { stream: true });
      }
      content += decoder.decode();
      return JSON.parse(content) as unknown;
    } catch (error) {
      if (error instanceof ClientError) throw error;
      throw new ClientError("Unable to read server response.");
    } finally { await reader.cancel().catch(() => undefined); reader.releaseLock(); }
  }

  private async request(path: string, method: string, body?: unknown, signal?: AbortSignal): Promise<Response> {
    const combined = AbortSignal.any([this.closed.signal, AbortSignal.timeout(150_000), ...(signal ? [signal] : [])]);
    let response: Response;
    try {
      response = await fetch(this.endpoint + path, {
        method, redirect: "error", signal: combined,
        headers: { Authorization: `Bearer ${this.token}`, ...(body !== undefined ? { "Content-Type": "application/json" } : {}) },
        ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
      });
    } catch { throw new ClientError(combined.aborted ? "Request canceled or timed out." : "Cannot reach the configured local server."); }
    if (!response.ok) {
      await response.body?.cancel();
      throw new ClientError(`Server request failed (HTTP ${response.status}). Provider details are not displayed.`);
    }
    return response;
  }
}