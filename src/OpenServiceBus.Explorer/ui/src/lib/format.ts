// Formatting + entity-address helpers, ported 1:1 from the original explorer.

/** "[d.]HH:MM:SS" TimeSpan string -> "Xs" / "Xm" / "Xh" / "Xd". */
export function humanTime(ts: string | null | undefined): string {
  if (!ts) return "∞";
  const m = /^(?:(\d+)\.)?(\d{2}):(\d{2}):(\d{2})/.exec(ts);
  if (!m) return ts;
  const days = parseInt(m[1] ?? "0", 10);
  const secs = days * 86400 + parseInt(m[2], 10) * 3600 + parseInt(m[3], 10) * 60 + parseInt(m[4], 10);
  if (secs % 86400 === 0 && secs >= 86400) return secs / 86400 + "d";
  if (secs % 3600 === 0 && secs >= 3600) return secs / 3600 + "h";
  if (secs % 60 === 0 && secs >= 60) return secs / 60 + "m";
  return secs + "s";
}

/** ISO timestamp -> relative ("in 25s" / "3m ago") or local time. */
export function formatTime(iso: string | null | undefined): string {
  if (!iso) return "-";
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return iso;
  const delta = Math.round((t - Date.now()) / 1000);
  const abs = Math.abs(delta);
  if (abs < 90) return delta >= 0 ? `in ${abs}s` : `${abs}s ago`;
  if (abs < 5400) return delta >= 0 ? `in ${Math.round(abs / 60)}m` : `${Math.round(abs / 60)}m ago`;
  return new Date(iso).toLocaleString();
}

/** Seconds -> "HH:MM:SS" TimeSpan string for create payloads. */
export function secondsToTimeSpan(seconds: number): string {
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = seconds % 60;
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${pad(h)}:${pad(m)}:${pad(s)}`;
}

// ---- entity addressing (string conventions shared with the backend) ----

export const DLQ_SUFFIX = "/$DeadLetterQueue";

export type EntityKind = "queue" | "topic" | "subscription";

export type Selected = {
  kind: EntityKind;
  /** queue name, topic name, or topic name for subscriptions */
  name: string;
  /** subscription name when kind === "subscription" */
  sub?: string;
};

export function entityAddress(sel: Selected): string {
  return sel.kind === "subscription" ? `${sel.name}/Subscriptions/${sel.sub}` : sel.name;
}

export function dlqAddress(sel: Selected): string {
  return entityAddress(sel) + DLQ_SUFFIX;
}

export function transferDlqAddress(sel: Selected): string {
  return entityAddress(sel) + "/$Transfer" + DLQ_SUFFIX;
}

export function displayName(sel: Selected): string {
  return sel.kind === "subscription" ? `${sel.name} / ${sel.sub}` : sel.name;
}

export function isMainQueue(name: string): boolean {
  return !name.endsWith(DLQ_SUFFIX) && !name.includes("/Subscriptions/");
}
