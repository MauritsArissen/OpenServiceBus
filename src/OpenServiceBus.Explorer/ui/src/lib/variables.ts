// Client-side mirror of the backend's DynamicVariables grammar (CannedMessages/
// DynamicVariables.cs). Used to highlight tokens while composing and to explain them on
// hover - resolution itself always happens server-side at send time.

export type VariableToken = {
  raw: string;
  start: number;
  end: number;
  /** "dynamic" = built-in {{$...}}; "env" = plain {{name}} from the active environment. */
  kind: "dynamic" | "env";
  valid: boolean;
  /** Dynamic: what it does. Env: the active environment's value, or why it is unresolved. */
  description: string;
};

export type LegendEntry = { syntax: string; description: string; example: string };

export type LegendGroup = { title: string; entries: LegendEntry[] };

export const LEGEND_GROUPS: LegendGroup[] = [
  {
    title: "Environment",
    entries: [
      {
        syntax: "{{yourKey}}",
        description: "The active environment's value for 'yourKey' - blue when it resolves, amber when the active environment has no such value. Values may themselves contain dynamic variables.",
        example: "cardnumber -> 123400000 while 'Card of Alice' is active",
      },
    ],
  },
  {
    title: "Uniqueness & sequencing",
    entries: [
      { syntax: "{{$guid}}", description: "Random guid, lowercase; also 'upper' or 'lower'. Fresh per copy.", example: "8b88a4d0-4b11-490c-9981-dbb6e96df00d" },
      { syntax: "{{$ulid}}", description: "Time-sortable unique id - send order stays visible when peeking.", example: "01J5ZK7Q2M3N4P5Q6R7S8T9V0W" },
      { syntax: "{{$sequence}}", description: "Incrementing counter, optionally '{{$sequence 100}}' to pick the start. Scoped per entity + template; survives across sends until the Explorer restarts (the demo's 30-minute reset restarts it).", example: "1, 2, 3, …" },
      { syntax: "{{$index}}", description: "Zero-based copy index within a multi-count send.", example: "count 5 gives 0..4" },
    ],
  },
  {
    title: "Time",
    entries: [
      { syntax: "{{$datetime iso8601}}", description: "Current UTC time; formats: iso8601, rfc1123, unix, unixms.", example: "2026-08-23T12:00:00.0000000Z" },
      { syntax: "{{$datetime 'yyyy-MM-dd'}}", description: "Custom .NET format string, in single quotes (spaces allowed).", example: "2026-08-23" },
      { syntax: "{{$datetime iso8601 -5d}}", description: "Optional offset: [+-]N plus y M w d h m s (capital M = months).", example: "-5d, 3h, +30m, 2w, -1M, 1y" },
      { syntax: "{{$timestamp}}", description: "Unix seconds shorthand.", example: "1787400000" },
    ],
  },
  {
    title: "Randomness",
    entries: [
      { syntax: "{{$randomInt}}", description: "Random integer 0..1000, or '{{$randomInt min max}}' inclusive.", example: "{{$randomInt 5 10}} -> 7" },
      { syntax: "{{$randomDouble min max}}", description: "Random decimal in range; optional third argument = decimals (default 2).", example: "{{$randomDouble 1.5 9.9 3}} -> 4.271" },
      { syntax: "{{$randomBoolean}}", description: "true or false.", example: "true" },
      { syntax: "{{$randomAlphaNumeric n}}", description: "Random letters+digits of length n; '{{$randomHex n}}' for hex.", example: "{{$randomHex 8}} -> 3f9a01bc" },
      { syntax: "{{$randomChoice a|b|c}}", description: "One of the listed values - ideal for enums like regions or statuses.", example: "eu|us|apac -> us" },
    ],
  },
  {
    title: "Utilities",
    entries: [
      { syntax: "{{$randomBase64 bytes}}", description: "Random blob of exactly N bytes, base64-encoded - payloads of a precise size.", example: "{{$randomBase64 1024}}" },
      { syntax: "{{$repeat 'text' n}}", description: "Deterministic padding: the quoted text repeated n times.", example: "{{$repeat 'x' 1024}} = 1 KB of x's" },
    ],
  },
];

const TOKEN = /\{\{\$([a-zA-Z][a-zA-Z0-9]*)([^{}]*)\}\}/g;
const ENV_TOKEN = /\{\{(?!\$)\s*([^{}\s]+)\s*\}\}/g;
const OFFSET = /^[+-]?\d+[yMwdhms]$/;

const UNIT_WORD: Record<string, string> = {
  y: "year(s)", M: "month(s)", w: "week(s)", d: "day(s)", h: "hour(s)", m: "minute(s)", s: "second(s)",
};

type ParsedArg = { value: string; quoted: boolean };

function parseArgs(raw: string): ParsedArg[] | null {
  const args: ParsedArg[] = [];
  let current = "";
  let inQuotes = false;
  let hasCurrent = false;
  let wasQuoted = false;
  for (const c of raw) {
    if (c === "'") {
      inQuotes = !inQuotes;
      hasCurrent = true;
      wasQuoted = true;
      continue;
    }
    if (!inQuotes && /\s/.test(c)) {
      if (hasCurrent) args.push({ value: current, quoted: wasQuoted });
      current = "";
      hasCurrent = false;
      wasQuoted = false;
      continue;
    }
    current += c;
    hasCurrent = true;
  }
  if (inQuotes) return null;
  if (hasCurrent) args.push({ value: current, quoted: wasQuoted });
  return args;
}

const intArg = (v: string) => /^-?\d+$/.test(v);
const floatArg = (v: string) => /^-?\d+(\.\d+)?$/.test(v);

function describeOffsetTail(format: string, args: ParsedArg[]): { valid: boolean; description: string } {
  if (args.length === 0) return { valid: true, description: `UTC now, ${format}` };
  if (!OFFSET.test(args[0].value)) {
    return { valid: false, description: `bad offset '${args[0].value}' - use [+-]N plus y M w d h m s, like -5d or 3h` };
  }
  const amount = args[0].value.replace(/[yMwdhms]$/, "");
  const unit = UNIT_WORD[args[0].value.slice(-1)];
  return { valid: true, description: `UTC now ${amount.startsWith("-") ? amount : "+" + amount.replace("+", "")} ${unit}, ${format}` };
}

function describe(name: string, rawArgs: string): { valid: boolean; description: string } {
  const parsed = parseArgs(rawArgs);
  if (parsed === null) return { valid: false, description: "unbalanced quotes in the arguments" };
  const args = parsed.map((a) => a.value);
  switch (name.toLowerCase()) {
    case "guid":
      if (args.length === 0) return { valid: true, description: "random guid, lowercase - fresh per copy" };
      if (args.length === 1 && (args[0] === "upper" || args[0] === "lower")) {
        return { valid: true, description: `random guid, ${args[0]}case - fresh per copy` };
      }
      return { valid: false, description: "guid takes at most one argument: upper or lower" };
    case "ulid":
      return args.length === 0
        ? { valid: true, description: "time-sortable unique id (ULID) - fresh per copy" }
        : { valid: false, description: "ulid takes no arguments" };
    case "sequence":
      if (args.length === 0) return { valid: true, description: "incrementing counter starting at 1, per entity + template" };
      if (args.length === 1 && intArg(args[0])) return { valid: true, description: `incrementing counter starting at ${args[0]}, per entity + template` };
      return { valid: false, description: "sequence takes at most one integer argument: the start value" };
    case "index":
      return args.length === 0
        ? { valid: true, description: "zero-based copy index within a multi-count send" }
        : { valid: false, description: "index takes no arguments" };
    case "datetime": {
      if (args.length === 0 || args.length > 2) {
        return { valid: false, description: "datetime needs a format: iso8601, rfc1123, unix, unixms or a quoted .NET format" };
      }
      const known: Record<string, string> = { iso8601: "ISO 8601", rfc1123: "RFC 1123", unix: "unix seconds", unixms: "unix milliseconds" };
      const format = known[args[0].toLowerCase()];
      if (format) return describeOffsetTail(format, parsed.slice(1));
      if (parsed[0].quoted) return describeOffsetTail(`format '${args[0]}'`, parsed.slice(1));
      return { valid: false, description: `unknown datetime format '${args[0]}' - use iso8601, rfc1123, unix, unixms, or quote a .NET format like '{{$datetime 'yyyy-MM-dd'}}'` };
    }
    case "timestamp":
      return args.length === 0
        ? { valid: true, description: "unix seconds" }
        : { valid: false, description: "timestamp takes no arguments" };
    case "randomint":
      if (args.length === 0) return { valid: true, description: "random integer 0..1000" };
      if (args.length === 2 && intArg(args[0]) && intArg(args[1]) && Number(args[0]) <= Number(args[1])) {
        return { valid: true, description: `random integer ${args[0]}..${args[1]} (inclusive)` };
      }
      return { valid: false, description: "randomInt takes no arguments or 'min max' with min <= max" };
    case "randomdouble":
      if ((args.length === 2 || args.length === 3)
        && floatArg(args[0]) && floatArg(args[1]) && Number(args[0]) <= Number(args[1])
        && (args.length === 2 || (intArg(args[2]) && Number(args[2]) >= 0 && Number(args[2]) <= 15))) {
        return { valid: true, description: `random decimal ${args[0]}..${args[1]}, ${args.length === 3 ? args[2] : 2} decimal(s)` };
      }
      return { valid: false, description: "randomDouble takes 'min max' and an optional decimals count (0..15)" };
    case "randomboolean":
      return args.length === 0
        ? { valid: true, description: "true or false" }
        : { valid: false, description: "randomBoolean takes no arguments" };
    case "randomalphanumeric":
    case "randomhex": {
      const alphabet = name.toLowerCase() === "randomhex" ? "hex" : "letters+digits";
      if (args.length === 1 && intArg(args[0]) && Number(args[0]) >= 1) {
        return { valid: true, description: `random ${alphabet} string of length ${args[0]}` };
      }
      return { valid: false, description: `${name} takes one argument: the length (>= 1)` };
    }
    case "randomchoice": {
      const choices = rawArgs.trim().split("|").map((c) => c.trim());
      if (choices.length >= 2 && choices.every((c) => c.length > 0)) {
        return { valid: true, description: `one of: ${choices.join(", ")}` };
      }
      return { valid: false, description: "randomChoice needs at least two |-separated values" };
    }
    case "randombase64":
      if (args.length === 1 && intArg(args[0]) && Number(args[0]) >= 1) {
        return { valid: true, description: `${args[0]} random byte(s), base64-encoded` };
      }
      return { valid: false, description: "randomBase64 takes one argument: the byte count (>= 1)" };
    case "repeat":
      if (args.length === 2 && args[0].length > 0 && intArg(args[1]) && Number(args[1]) >= 1) {
        return { valid: true, description: `'${args[0]}' repeated ${args[1]} time(s)` };
      }
      return { valid: false, description: "repeat takes a quoted text and a count, like {{$repeat 'x' 1024}}" };
    default:
      return { valid: false, description: `unknown variable '$${name}' - open the Variables guide for what is available` };
  }
}

export function tokenizeVariables(text: string, env: Record<string, string> | null = null): VariableToken[] {
  const tokens: VariableToken[] = [];
  for (const match of text.matchAll(TOKEN)) {
    const { valid, description } = describe(match[1], match[2] ?? "");
    tokens.push({ raw: match[0], start: match.index, end: match.index + match[0].length, kind: "dynamic", valid, description });
  }
  for (const match of text.matchAll(ENV_TOKEN)) {
    const key = match[1];
    const resolved = env !== null && key in env;
    tokens.push({
      raw: match[0],
      start: match.index,
      end: match.index + match[0].length,
      kind: "env",
      valid: resolved,
      description: resolved
        ? env[key]
        : env === null
          ? "no active environment - sent verbatim"
          : "no value in the active environment - sent verbatim",
    });
  }
  tokens.sort((a, b) => a.start - b.start);
  return tokens;
}

/** Distinct tokens across a set of named fields (body, MessageId, ...), field names attached. */
export function collectVariables(
  fields: Record<string, string>,
  env: Record<string, string> | null = null,
): (VariableToken & { fields: string[] })[] {
  const byRaw = new Map<string, VariableToken & { fields: string[] }>();
  for (const [field, text] of Object.entries(fields)) {
    if (!text || !text.includes("{{")) continue;
    for (const token of tokenizeVariables(text, env)) {
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
