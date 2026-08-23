// Client-side mirror of the backend's DynamicVariables grammar (CannedMessages/
// DynamicVariables.cs). Used to highlight tokens while composing and to explain them on
// hover - resolution itself always happens server-side at send time.

export type VariableToken = {
  raw: string;
  start: number;
  end: number;
  valid: boolean;
  /** Human explanation ("random guid, uppercase") or the validation error. */
  description: string;
};

export type LegendEntry = { syntax: string; description: string; example: string };

export const VARIABLE_LEGEND: LegendEntry[] = [
  { syntax: "{{$guid}}", description: "Random guid, lowercase. A fresh one per message copy.", example: "8b88a4d0-4b11-490c-9981-dbb6e96df00d" },
  { syntax: "{{$guid upper}}", description: "Random guid, uppercase.", example: "8B88A4D0-4B11-490C-9981-DBB6E96DF00D" },
  { syntax: "{{$datetime iso8601}}", description: "Current UTC time, ISO 8601 round-trip format.", example: "2026-08-23T12:00:00.0000000Z" },
  { syntax: "{{$datetime rfc1123}}", description: "Current UTC time, RFC 1123 format.", example: "Sun, 23 Aug 2026 12:00:00 GMT" },
  { syntax: "{{$datetime iso8601 -5d}}", description: "With an offset: [+-]N plus y M w d h m s (capital M = months).", example: "5 days ago; 3h, +30m, 2w, -1M, 1y" },
];

const TOKEN = /\{\{\$([a-zA-Z]+)((?:\s+[^\s{}]+)*)\s*\}\}/g;
const OFFSET = /^[+-]?\d+[yMwdhms]$/;

const UNIT_WORD: Record<string, string> = {
  y: "year(s)", M: "month(s)", w: "week(s)", d: "day(s)", h: "hour(s)", m: "minute(s)", s: "second(s)",
};

function describe(name: string, argText: string): { valid: boolean; description: string } {
  const args = argText.trim() === "" ? [] : argText.trim().split(/\s+/);
  switch (name.toLowerCase()) {
    case "guid":
      if (args.length === 0) return { valid: true, description: "random guid, lowercase - fresh per copy" };
      if (args.length === 1 && (args[0] === "upper" || args[0] === "lower")) {
        return { valid: true, description: `random guid, ${args[0]}case - fresh per copy` };
      }
      return { valid: false, description: "guid takes at most one argument: upper or lower" };
    case "datetime": {
      if (args.length === 0 || args.length > 2) {
        return { valid: false, description: "datetime needs a format: iso8601 or rfc1123, plus an optional offset" };
      }
      const format = args[0].toLowerCase();
      if (format !== "iso8601" && format !== "rfc1123") {
        return { valid: false, description: `unknown datetime format '${args[0]}' - use iso8601 or rfc1123` };
      }
      if (args.length === 1) return { valid: true, description: `UTC now, ${format === "iso8601" ? "ISO 8601" : "RFC 1123"}` };
      if (!OFFSET.test(args[1])) {
        return { valid: false, description: `bad offset '${args[1]}' - use [+-]N plus y M w d h m s, like -5d or 3h` };
      }
      const amount = args[1].replace(/[yMwdhms]$/, "");
      const unit = UNIT_WORD[args[1].slice(-1)];
      return { valid: true, description: `UTC now ${amount.startsWith("-") ? amount : "+" + amount.replace("+", "")} ${unit}, ${format === "iso8601" ? "ISO 8601" : "RFC 1123"}` };
    }
    default:
      return { valid: false, description: `unknown variable '$${name}' - see the legend for what is available` };
  }
}

export function tokenizeVariables(text: string): VariableToken[] {
  const tokens: VariableToken[] = [];
  for (const match of text.matchAll(TOKEN)) {
    const { valid, description } = describe(match[1], match[2] ?? "");
    tokens.push({ raw: match[0], start: match.index, end: match.index + match[0].length, valid, description });
  }
  return tokens;
}

/** Distinct tokens across a set of named fields (body, MessageId, ...), field names attached. */
export function collectVariables(fields: Record<string, string>): (VariableToken & { fields: string[] })[] {
  const byRaw = new Map<string, VariableToken & { fields: string[] }>();
  for (const [field, text] of Object.entries(fields)) {
    if (!text || !text.includes("{{$")) continue;
    for (const token of tokenizeVariables(text)) {
      const existing = byRaw.get(token.raw);
      if (existing) {
        if (!existing.fields.includes(field)) existing.fields.push(field);
      } else {
        byRaw.set(token.raw, { ...token, fields: [field] });
      }
    }
  }
  return [...byRaw.values()];
}
