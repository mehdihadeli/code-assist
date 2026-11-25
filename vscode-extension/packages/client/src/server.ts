import { spawn, type ChildProcess } from "node:child_process";
import { randomBytes } from "node:crypto";
import { CodeAssistClient, ClientError } from "./client";

export class ManagedServer {
  private stopped = false;

  private constructor(private readonly process: ChildProcess, readonly client: CodeAssistClient) {
    process.once("exit", () => client.dispose());
  }

  get alive(): boolean { return !this.stopped && this.process.exitCode === null && this.process.signalCode === null; }

  static async start(executable: string, arguments_: readonly string[], cwd: string, signal?: AbortSignal): Promise<ManagedServer> {
    if (!executable.trim() || executable.length > 4096 || arguments_.length > 32 || arguments_.some(value => typeof value !== "string" || value.length > 4096))
      throw new ClientError("Invalid runtime executable or arguments.");
    if (signal?.aborted) throw new ClientError("Connection canceled.");
    const token = randomBytes(32).toString("base64url");
    const inherited = Object.fromEntries(Object.entries(process.env).filter(([name]) => name.toLowerCase() !== "host__listen__http__token"));
    const child = spawn(executable, [...arguments_, "--listen", "127.0.0.1:0"], {
      cwd, shell: false, windowsHide: true, stdio: ["ignore", "pipe", "pipe"],
      env: { ...inherited, Host__Listen__Http__Token: token, PWD: cwd, NO_COLOR: "1", DOTNET_NOLOGO: "1" },
    });
    child.stderr?.resume();
    let address: string;
    try {
      address = await new Promise<string>((resolve, reject) => {
        let pending = "";
        const timer = setTimeout(() => fail(), 20_000);
        const cleanup = () => {
          clearTimeout(timer);
          signal?.removeEventListener("abort", fail);
          child.removeListener("error", fail);
          child.removeListener("exit", fail);
          child.stdout?.removeListener("data", data);
        };
        const fail = () => { cleanup(); reject(new ClientError("Runtime failed to start. Check the trusted server executable and runtime configuration.")); };
        const data = (chunk: string) => {
          pending += chunk;
          if (pending.length > 65_536) { fail(); return; }
          let newline: number;
          while ((newline = pending.indexOf("\n")) >= 0) {
            const line = pending.slice(0, newline).trim();
            pending = pending.slice(newline + 1);
            let ready: unknown;
            try { ready = JSON.parse(line); } catch { fail(); return; }
            if (!ready || typeof ready !== "object" || !("kind" in ready) || ready.kind !== "ready" ||
                !("protocol" in ready) || ready.protocol !== "1" || !("endpoint" in ready) ||
                typeof ready.endpoint !== "string" || !/^http:\/\/127\.0\.0\.1:[1-9][0-9]*$/.test(ready.endpoint)) {
              fail(); return;
            }
            cleanup(); resolve(ready.endpoint); return;
          }
        };
        child.stdout?.setEncoding("utf8");
        child.stdout?.on("data", data);
        child.once("error", fail);
        child.once("exit", fail);
        signal?.addEventListener("abort", fail, { once: true });
        if (signal?.aborted) fail();
      });
    } catch (error) { child.kill(); throw error; }
    child.stdout?.resume();
    child.on("error", () => undefined);
    let server: ManagedServer;
    try { server = new ManagedServer(child, new CodeAssistClient(address, token)); }
    catch (error) { child.kill(); throw error; }
    try {
      await server.client.check(signal);
      if (!server.alive || signal?.aborted) throw new ClientError("Runtime exited during connection.");
      return server;
    } catch (error) { await server.dispose(); throw error; }
  }

  async dispose(): Promise<void> {
    this.client.dispose();
    if (!this.alive) { this.stopped = true; return; }
    this.stopped = true;
    await new Promise<void>(resolve => {
      const timer = setTimeout(() => { this.process.kill("SIGKILL"); resolve(); }, 3000);
      this.process.once("exit", () => { clearTimeout(timer); resolve(); });
      this.process.kill();
    });
  }
}