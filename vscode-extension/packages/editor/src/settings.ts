import { ClientError } from "@codeassist/client";

export function connectionSetting(key: string, fallback: string | string[], userValue: unknown, environment: NodeJS.ProcessEnv = process.env): string | string[] {
  const name = `codeAssist__${key.replaceAll(".", "__")}`.toLowerCase();
  const entries = Object.entries(environment).filter((entry): entry is [string, string] => entry[1] !== undefined)
    .map(([path, value]) => [path.toLowerCase(), value] as const);
  const whole = entries.find(([path]) => path === name);
  let value: unknown = userValue ?? fallback;
  if (whole) {
    try { value = Array.isArray(fallback) ? JSON.parse(whole[1]) as unknown : whole[1]; }
    catch { throw new ClientError("Invalid connection environment override."); }
  }
  if (Array.isArray(fallback)) {
    if (!Array.isArray(value)) throw new ClientError("Runtime arguments must be a string array.");
    const result: unknown[] = [...value];
    for (const [path, item] of entries.filter(([path]) => path.startsWith(name + "__"))) {
      const index = path.slice(name.length + 2);
      if (!/^(0|[1-9][0-9]?)$/.test(index) || Number(index) > 31) throw new ClientError("Invalid runtime argument index.");
      result[Number(index)] = item;
    }
    if (result.length > 32 || Array.from(result).some(item => typeof item !== "string")) throw new ClientError("Runtime arguments must be a contiguous string array.");
    return result as string[];
  }
  if (typeof value !== "string") throw new ClientError("Connection setting must be a string.");
  return value;
}