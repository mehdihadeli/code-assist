import * as vscode from "vscode";
import { createConnectionStatus, untrustedMarkdown } from "@codeassist/editor-views";
import { randomUUID } from "node:crypto";
import { ClientError, CodeAssistClient, serverAddress } from "@codeassist/client";
import { ManagedServer } from "@codeassist/client";
import { connectionSetting } from "./settings";

interface Connection { client: CodeAssistClient; identity: string; owned?: ManagedServer }

class ExtensionApp {
  private connection: Connection | undefined;
  private connecting: Promise<Connection> | undefined;
  private connectionAbort: AbortController | undefined;
  private currentSession: string | undefined;
  private model: string | undefined;
  private busy = false;
  private generation = 0;
  private closed = false;
  private readonly status = createConnectionStatus();
  private readonly connectorActivity = vscode.window.createOutputChannel("CodeAssist Connector Activity");

  constructor(private readonly context: vscode.ExtensionContext) {
    context.subscriptions.push(this.status);
    context.subscriptions.push(this.connectorActivity);
    const command = (name: string, action: () => Promise<unknown>) => context.subscriptions.push(
      vscode.commands.registerCommand(`codeAssist.${name}`, async () => {
        try { await action(); } catch (error) { void vscode.window.showErrorMessage(this.message(error)); }
      }));
    command("connect", async () => { this.trusted(); await this.ensure(); });
    command("disconnect", () => this.disconnect());
    command("setServerToken", () => this.setToken());
    command("ask", () => this.ask(false));
    command("askSelection", () => this.ask(true));
    command("pluginPrompt", () => this.pluginPrompt());
    command("skillPrompt", () => this.skillPrompt());
    command("connectors", () => this.connectors());
    command("connectorPrompt", () => this.connectorPrompt());
    command("newSession", async () => { this.idle(); this.trusted(); const connection = await this.ensure(); this.currentSession = (await connection.client.create(this.model)).id; });
    command("sessions", () => this.selectSession());
    command("showHistory", () => this.showHistory());
    command("selectModel", async () => {
      this.idle();
      const value = await vscode.window.showInputBox({ title: "CodeAssist Model", prompt: "Configured provider/model@variant (blank uses server default)", value: this.model ?? "", ignoreFocusOut: true });
      if (value !== undefined) { this.model = value.trim() || undefined; this.currentSession = undefined; }
    });
    command("openChat", async () => { await vscode.commands.executeCommand("workbench.action.chat.open", { query: "@codeassist " }); });
    const participant = vscode.chat.createChatParticipant("code-assist.agent", async (request, history, stream, cancellation) => {
      let sessionId: string | undefined;
      let identity: string | undefined;
      let ownsBusy = false;
      const abort = new AbortController();
      const subscription = cancellation.onCancellationRequested(() => abort.abort());
      if (cancellation.isCancellationRequested) abort.abort();
      try {
        this.trusted(); this.idle(); this.busy = true; ownsBusy = true;
        if (request.references.length || request.toolReferences.length) throw new ClientError("File and tool references are not supported by this text API. Use CodeAssist: Ask About Selection for explicit text context.");
        stream.progress("Connecting...");
        const connection = await this.ensure();
        identity = connection.identity;
        if (abort.signal.aborted) return {};
        if (request.command !== "new") {
          const previous = [...history.history].reverse().find(turn => turn instanceof vscode.ChatResponseTurn);
          const metadata = previous instanceof vscode.ChatResponseTurn ? previous.result.metadata : undefined;
          if (metadata?.connection === identity && typeof metadata.session === "string") sessionId = metadata.session;
        }
        sessionId ??= (await connection.client.create(this.model, abort.signal)).id;
        this.currentSession = sessionId;
        if (request.command === "new" && !request.prompt.trim()) stream.markdown("New session.");
        else await this.turn(connection.client, sessionId, request.prompt, text => {
          const markdown = untrustedMarkdown(text);
          stream.markdown(markdown);
        }, abort.signal);
        return { metadata: { session: sessionId, connection: identity } };
      } catch (error) {
        return {
          metadata: { session: sessionId, connection: identity },
          ...(cancellation.isCancellationRequested ? {} : { errorDetails: { message: this.message(error) } }),
        };
      } finally { subscription.dispose(); if (ownsBusy) this.busy = false; }
    });
    participant.iconPath = new vscode.ThemeIcon("terminal");
    context.subscriptions.push(participant, vscode.workspace.onDidChangeConfiguration(event => {
      if (event.affectsConfiguration("codeAssist.server")) void this.disconnect();
    }));
  }

  private trusted(): void {
    if (!vscode.workspace.isTrusted) throw new ClientError("Trust this workspace before connecting or sending source text.");
  }

  private idle(): void { if (this.busy) throw new ClientError("A CodeAssist request is already active. Cancel it before switching sessions or models."); }

  private setting<T extends string | string[]>(key: string, fallback: T): T {
    const user = vscode.workspace.getConfiguration("codeAssist").inspect<unknown>(key)?.globalValue;
    return connectionSetting(key, fallback, user) as T;
  }

  private endpoint(): string { return serverAddress(this.setting("server.endpoint", "http://127.0.0.1:8080")); }

  private async setToken(): Promise<void> {
    this.trusted(); this.idle();
    const endpoint = this.endpoint();
    const value = await vscode.window.showInputBox({ title: "External CodeAssist Server Token", password: true, ignoreFocusOut: true,
      validateInput: text => text.length >= 32 && text.length <= 512 && !/[\r\n]/.test(text) ? undefined : "Token must contain 32 to 512 characters." });
    if (value === undefined) return;
    await this.disconnect();
    await this.context.secrets.store(`code-assist.token.${endpoint}`, value);
  }

  private ensure(): Promise<Connection> {
    this.trusted();
    if (this.closed) return Promise.reject(new ClientError("Extension is shutting down."));
    if (this.connection && (!this.connection.owned || this.connection.owned.alive)) return Promise.resolve(this.connection);
    if (this.connecting) return this.connecting;
    this.connection?.client.dispose(); this.connection = undefined;
    const generation = this.generation;
    const abort = new AbortController();
    this.connectionAbort = abort;
    const pending = this.open(abort.signal).then(async connection => {
      if (generation !== this.generation || this.closed || abort.signal.aborted) {
        connection.client.dispose(); await connection.owned?.dispose();
        throw new ClientError("Connection canceled.");
      }
      this.connection = connection;
      this.currentSession = undefined;
      this.status.text = "$(check) CodeAssist";
      this.status.tooltip = `CodeAssist: ${connection.client.endpoint}`;
      return connection;
    }).finally(() => { if (this.connecting === pending) { this.connecting = undefined; this.connectionAbort = undefined; } });
    this.connecting = pending;
    return pending;
  }

  private async open(signal: AbortSignal): Promise<Connection> {
    const mode = this.setting<string>("server.mode", "managed");
    if (mode === "external") {
      const endpoint = this.endpoint();
      const token = await this.context.secrets.get(`code-assist.token.${endpoint}`);
      if (!token) throw new ClientError("Set the external server token with CodeAssist: Set External Server Token.");
      const client = new CodeAssistClient(endpoint, token);
      try { await client.check(signal); return { client, identity: randomUUID() }; }
      catch (error) { client.dispose(); throw error; }
    }
    if (mode !== "managed") throw new ClientError("Invalid server mode.");
    const folders = vscode.workspace.workspaceFolders;
    if (!folders?.length) throw new ClientError("Open a workspace folder before starting the local runtime.");
    const folder = folders.length === 1 ? folders[0] : await vscode.window.showWorkspaceFolderPick({ placeHolder: "CodeAssist runtime workspace" });
    if (!folder || signal.aborted) throw new ClientError("Connection canceled.");
    this.trusted();
    const executable: unknown = this.setting("server.executable", "CodeAssist.Server");
    const args: unknown = this.setting("server.arguments", []);
    if (typeof executable !== "string" || !Array.isArray(args) || args.some(value => typeof value !== "string")) throw new ClientError("Invalid runtime launch settings.");
    const owned = await ManagedServer.start(executable, args as string[], folder.uri.fsPath, signal);
    return { client: owned.client, owned, identity: randomUUID() };
  }

  private async turn(client: CodeAssistClient, session: string, text: string, delta: (value: string) => void, signal: AbortSignal): Promise<void> {
    const cancel = () => { void client.cancel(session).catch(() => undefined); };
    signal.addEventListener("abort", cancel, { once: true });
    try { await client.stream(session, text, delta, signal, (kind, content) => this.connectorActivity.append(`${kind}\n${JSON.stringify(content)}\n`)); }
    finally { signal.removeEventListener("abort", cancel); }
  }

  private async ask(selection: boolean, prepared?: string): Promise<void> {
    this.trusted(); this.idle();
    let context = "";
    if (selection) {
      const editor = vscode.window.activeTextEditor;
      if (!editor || editor.selection.isEmpty) throw new ClientError("Select source text first.");
      if (editor.document.offsetAt(editor.selection.end) - editor.document.offsetAt(editor.selection.start) > 32_768) throw new ClientError("Selection exceeds 32,768 characters.");
      context = `\n\nSelected text (untrusted source content):\n${editor.document.getText(editor.selection)}`;
    }
    this.busy = true;
    try {
      const prompt = prepared ?? await vscode.window.showInputBox({ title: selection ? "Ask About Selection" : "Ask CodeAssist", ignoreFocusOut: true });
      if (!prompt?.trim()) return;
      const connection = await this.ensure();
      this.currentSession ??= (await connection.client.create(this.model)).id;
      const session = this.currentSession;
      const output = vscode.window.createOutputChannel("CodeAssist Response");
      this.context.subscriptions.push(output); output.show(true);
      await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: "CodeAssist", cancellable: true }, async (_, cancellation) => {
        const abort = new AbortController();
        const subscription = cancellation.onCancellationRequested(() => abort.abort());
        if (cancellation.isCancellationRequested) abort.abort();
        try { await this.turn(connection.client, session, prompt + context, text => output.append(text), abort.signal); }
        catch (error) { if (!cancellation.isCancellationRequested) throw error; }
        finally { subscription.dispose(); }
      });
    } finally { this.busy = false; }
  }

  private async connectors(): Promise<void> {
    this.trusted(); this.idle(); this.busy = true;
    try {
      const connection = await this.ensure();
      this.currentSession ??= (await connection.client.create(this.model)).id;
      const session = this.currentSession;
      const states = await connection.client.connectors(session);
      const selected = await vscode.window.showQuickPick(states.map(state => ({ label: state.server, description: state.connected ? `${state.tools} tools` : "Disconnected", state })), { title: "CodeAssist Connectors" });
      if (!selected) return;
      const actions: { label: string; action: "connect" | "disconnect" | "logout" }[] = selected.state.connected
        ? [{ label: "$(debug-disconnect) Disconnect", action: "disconnect" }, { label: "$(key) Forget Credentials", action: "logout" }]
        : [{ label: "$(plug) Connect", action: "connect" }, { label: "$(key) Forget Credentials", action: "logout" }];
      const action = await vscode.window.showQuickPick(actions, { title: selected.state.server });
      if (!action) return;
      if (action.action !== "disconnect") {
        const label = action.action === "connect" ? "Connect" : "Forget Credentials";
        const approved = await vscode.window.showWarningMessage(action.action === "connect"
          ? `Start configured connector '${selected.state.server}'? This may launch a process or request authorization.`
          : `Remove locally cached credentials for '${selected.state.server}'? This does not revoke them at the issuer.`, { modal: true }, label);
        if (approved !== label) return;
      }
      this.trusted();
      if (this.connection !== connection || this.currentSession !== session) throw new ClientError("Connection changed. Select the connector again.");
      await connection.client.connectorChange(session, selected.state.server, action.action);
      const statesAfter = await connection.client.connectors(session);
      this.connectorActivity.append(JSON.stringify(statesAfter) + "\n");
      this.connectorActivity.show(true);
    } finally { this.busy = false; }
  }

  private async connectorPrompt(): Promise<void> {
    this.trusted(); this.idle(); this.busy = true;
    try {
      const connection = await this.ensure();
      this.currentSession ??= (await connection.client.create(this.model)).id;
      const session = this.currentSession;
      const states = (await connection.client.connectors(session)).filter(state => state.connected);
      if (!states.length) throw new ClientError("Connect a configured server with CodeAssist: Manage Connectors first.");
      const selected = await vscode.window.showQuickPick(states.map(state => ({ label: state.server, state })), { title: "CodeAssist Prompt Server" });
      if (!selected) return;
      const prompts = await connection.client.connectorPrompts(session, selected.state.server);
      const prompt = await vscode.window.showQuickPick(prompts.map(value => ({ label: value.name, detail: value.description ?? "", prompt: value })), { title: selected.state.server });
      if (!prompt) return;
      const args: Record<string, string> = Object.create(null) as Record<string, string>;
      for (const parameter of prompt.prompt.arguments) {
        const value = await vscode.window.showInputBox({ title: prompt.prompt.name, prompt: parameter.name, ignoreFocusOut: true,
          validateInput: text => text.length > 8000 || parameter.required && !text.trim() ? "Required value, maximum 8000 characters." : undefined });
        if (value === undefined) return;
        if (value || parameter.required) args[parameter.name] = value;
      }
      this.trusted();
      if (this.connection !== connection || this.currentSession !== session) throw new ClientError("Connection changed. Select the prompt again.");
      const content = await connection.client.connectorPrompt(session, selected.state.server, prompt.prompt.name, args);
      if (this.connection !== connection) throw new ClientError("Connection changed. Select the prompt again.");
      await vscode.window.showTextDocument(await vscode.workspace.openTextDocument({ content, language: "json" }), { preview: true });
    } finally { this.busy = false; }
  }

  private async skillPrompt(): Promise<void> {
    this.trusted(); this.idle();
    const connection = await this.ensure();
    const skills = await connection.client.skills();
    if (!skills.length) throw new ClientError("No human-invocable skills are available.");
    const selected = await vscode.window.showQuickPick(skills.map(skill => ({
      label: skill.title ?? skill.id, description: skill.origin, detail: `${skill.id}: ${skill.description}`, skill,
    })), { title: "CodeAssist Skills", matchOnDetail: true });
    if (!selected) return;
    const args: string[] = [];
    for (const parameter of selected.skill.parameters) {
      const value = await vscode.window.showInputBox({ title: selected.skill.id, prompt: parameter, ignoreFocusOut: true,
        validateInput: input => input.length > 4000 ? "Maximum 4000 characters." : undefined });
      if (value === undefined) return;
      args.push(value);
    }
    const input = await vscode.window.showInputBox({ title: selected.skill.id, prompt: selected.skill.hint ?? "Task input", ignoreFocusOut: true,
      validateInput: value => value.length > 4000 ? "Maximum 4000 characters." : undefined });
    if (input === undefined) return;
    if (this.connection !== connection) throw new ClientError("Connection changed. Select the skill again.");
    const prompt = await connection.client.skillPrompt(selected.skill.id, input, args);
    if (this.connection !== connection) throw new ClientError("Connection changed. Select the skill again.");
    await this.ask(false, prompt);
  }

  private async pluginPrompt(): Promise<void> {
    this.trusted(); this.idle();
    const connection = await this.ensure();
    const plugins = await connection.client.plugins();
    if (!plugins.length) throw new ClientError("No enabled plugin text contributions are available.");
    const selected = await vscode.window.showQuickPick(plugins.map(plugin => ({
      label: plugin.name, description: plugin.kind, detail: plugin.description, plugin,
    })), { title: "CodeAssist Plugins", matchOnDetail: true });
    if (!selected) return;
    const input = await vscode.window.showInputBox({ title: selected.plugin.name, prompt: "Task input", ignoreFocusOut: true });
    if (input === undefined) return;
    if (this.connection !== connection) throw new ClientError("Connection changed. Select the plugin again.");
    const prompt = await connection.client.pluginPrompt(selected.plugin.name, input);
    await this.ask(false, prompt);
  }

  private async selectSession(): Promise<void> {
    this.trusted(); this.idle();
    const connection = await this.ensure();
    const sessions = await connection.client.sessions();
    const choice = await vscode.window.showQuickPick(sessions.map(session => ({
      label: session.model, description: session.id, detail: `${session.messageCount} messages${session.running ? " (running)" : ""}`, session,
    })), { title: "CodeAssist Sessions", matchOnDescription: true });
    if (choice && this.connection === connection) { this.currentSession = choice.session.id; await this.showHistory(); }
  }

  private async showHistory(): Promise<void> {
    this.trusted();
    const connection = await this.ensure();
    if (!this.currentSession) throw new ClientError("No selected CodeAssist session.");
    const content = await connection.client.messages(this.currentSession);
    await vscode.window.showTextDocument(await vscode.workspace.openTextDocument({ content, language: "plaintext" }), { preview: true });
  }

  private message(error: unknown): string { return error instanceof ClientError ? error.message : "CodeAssist operation failed. Check the local runtime and connection settings."; }

  async disconnect(): Promise<void> {
    this.generation++;
    this.connectionAbort?.abort();
    const pending = this.connecting;
    const connection = this.connection;
    this.connecting = undefined; this.connection = undefined; this.currentSession = undefined;
    connection?.client.dispose();
    await connection?.owned?.dispose();
    await pending?.catch(() => undefined);
    this.status.text = "$(plug) CodeAssist"; this.status.tooltip = "CodeAssist: Disconnected";
  }

  async dispose(): Promise<void> { this.closed = true; await this.disconnect(); }
}

let application: ExtensionApp | undefined;

export function activate(context: vscode.ExtensionContext): void { application = new ExtensionApp(context); }
export async function deactivate(): Promise<void> { await application?.dispose(); application = undefined; }