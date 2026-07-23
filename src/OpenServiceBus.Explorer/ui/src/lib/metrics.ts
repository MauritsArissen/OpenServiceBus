import type { MetricSample } from "@/lib/api";

export type Bucket = {
  t: number; // bucket end (unix seconds)
  // throughput (per-bucket deltas)
  newMessages: number;
  completed: number;
  deadLettered: number;
  // levels (value at bucket end)
  active: number;
  dlqActive: number;
};

/**
 * Turn raw samples into a fixed grid of buckets spanning the WHOLE window, so a chart
 * always shows the full time axis (zero-filled where no data exists). ~60 buckets.
 *
 * - Levels (active / dlqActive): the latest sample value at or before the bucket end;
 *   0 until the first sample exists (so the axis starts filled with zeros).
 * - Throughput (newMessages / completed / deadLettered): the increase in the cumulative
 *   counter across the bucket, i.e. value-at-bucket-end minus value-at-previous-bucket-end.
 *   Clamped at 0 so a broker restart (counter reset) doesn't produce a negative spike.
 */
export function bucketize(
  entity: MetricSample[],
  dlq: MetricSample[],
  windowSeconds: number,
  nowUnix: number,
): Bucket[] {
  const count = 60;
  const bucketSec = Math.max(15, Math.round(windowSeconds / count));
  const start = nowUnix - windowSeconds;

  const ent = [...entity].sort((a, b) => a.t - b.t);
  const dl = [...dlq].sort((a, b) => a.t - b.t);

  // Latest sample at or before time `at` (or null before any sample).
  const latest = (arr: MetricSample[], at: number): MetricSample | null => {
    let found: MetricSample | null = null;
    for (const s of arr) {
      if (s.t <= at) found = s;
      else break;
    }
    return found;
  };

  const buckets: Bucket[] = [];
  for (let i = 1; i <= count; i++) {
    const end = start + i * bucketSec;
    const prevEnd = end - bucketSec;

    const eNow = latest(ent, end);
    const ePrev = latest(ent, prevEnd);
    const dNow = latest(dl, end);
    const dPrev = latest(dl, prevEnd);

    const delta = (now: MetricSample | null, prev: MetricSample | null, key: "enqueued" | "completed") =>
      now && prev ? Math.max(0, now[key] - prev[key]) : 0;

    buckets.push({
      t: end,
      newMessages: delta(eNow, ePrev, "enqueued"),
      completed: delta(eNow, ePrev, "completed"),
      deadLettered: delta(dNow, dPrev, "enqueued"), // arrivals into the DLQ = dead-letter rate
      active: eNow?.active ?? 0,
      dlqActive: dNow?.active ?? 0,
    });
  }
  return buckets;
}
