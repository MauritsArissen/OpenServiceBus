import { ActivityIcon } from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";
import {
  Area, AreaChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis,
} from "recharts";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { explorerApi, type MetricSample } from "@/lib/api";
import { type Selected } from "@/lib/format";
import { cn } from "@/lib/utils";
import { entityAddress } from "@/store";

const WINDOWS = [
  { label: "30 min", seconds: 1800 },
  { label: "1 hour", seconds: 3600 },
  { label: "4 hours", seconds: 14400 },
  { label: "12 hours", seconds: 43200 },
  { label: "24 hours", seconds: 86400 },
];

const SERIES = [
  { key: "active", label: "Active", color: "var(--color-brand)" },
  { key: "deadLettered", label: "Dead-lettered", color: "oklch(0.62 0.2 25)" },
] as const;

type Row = { t: number; active?: number; deadLettered?: number };

export function MetricsTab({ sel }: { sel: Selected }) {
  const address = entityAddress(sel);
  const [windowSeconds, setWindowSeconds] = useState(1800);
  const [enabled, setEnabled] = useState<Record<string, boolean>>({ active: true, deadLettered: true });
  const [data, setData] = useState<{ active: MetricSample[]; deadLettered: MetricSample[] } | null>(null);
  const timer = useRef<number | null>(null);

  useEffect(() => {
    let cancelled = false;
    const load = async () => {
      try {
        const r = await explorerApi.metrics(address, windowSeconds);
        if (!cancelled) setData(r);
      } catch {
        if (!cancelled) setData({ active: [], deadLettered: [] });
      }
    };
    void load();
    timer.current = window.setInterval(load, 15000); // live updates as new samples land
    return () => {
      cancelled = true;
      if (timer.current) window.clearInterval(timer.current);
    };
  }, [address, windowSeconds]);

  // Merge the two series onto a shared timeline keyed by sample timestamp.
  const rows = useMemo<Row[]>(() => {
    if (!data) return [];
    const byT = new Map<number, Row>();
    for (const s of data.active) byT.set(s.t, { ...(byT.get(s.t) ?? { t: s.t }), active: s.active });
    for (const s of data.deadLettered) byT.set(s.t, { ...(byT.get(s.t) ?? { t: s.t }), deadLettered: s.active });
    return [...byT.values()].sort((a, b) => a.t - b.t);
  }, [data]);

  const fmtTime = (t: number) => {
    const d = new Date(t * 1000);
    return windowSeconds <= 14400
      ? d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
      : d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", hour12: false });
  };

  const hasData = rows.length > 0;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2">
        <div className="flex gap-1.5">
          {SERIES.map((s) => (
            <Button
              key={s.key}
              variant="outline"
              size="sm"
              onClick={() => setEnabled((e) => ({ ...e, [s.key]: !e[s.key] }))}
              className={cn("gap-1.5", !enabled[s.key] && "opacity-45")}
            >
              <span className="size-2.5 rounded-[3px]" style={{ background: s.color }} />
              {s.label}
            </Button>
          ))}
        </div>
        <Select value={String(windowSeconds)} onValueChange={(v) => setWindowSeconds(Number(v))}>
          <SelectTrigger className="ml-auto w-32">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {WINDOWS.map((w) => (
              <SelectItem key={w.seconds} value={String(w.seconds)}>
                Last {w.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>

      <Card>
        <CardContent className="pt-5">
          {!hasData ? (
            <div className="flex h-72 flex-col items-center justify-center text-center text-sm text-muted-foreground">
              <ActivityIcon className="mb-3 size-8 opacity-40" />
              <p className="font-medium text-foreground">Collecting metrics…</p>
              <p className="mt-1 max-w-sm">
                The Explorer samples message counts every 15 seconds. Points appear as they're
                recorded, filling the {WINDOWS.find((w) => w.seconds === windowSeconds)?.label} window over time.
              </p>
            </div>
          ) : (
            <ResponsiveContainer width="100%" height={300}>
              <AreaChart data={rows} margin={{ top: 8, right: 8, left: -12, bottom: 0 }}>
                <defs>
                  {SERIES.map((s) => (
                    <linearGradient key={s.key} id={`g-${s.key}`} x1="0" y1="0" x2="0" y2="1">
                      <stop offset="0%" stopColor={s.color} stopOpacity={0.35} />
                      <stop offset="100%" stopColor={s.color} stopOpacity={0.02} />
                    </linearGradient>
                  ))}
                </defs>
                <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" vertical={false} />
                <XAxis
                  dataKey="t"
                  tickFormatter={fmtTime}
                  tick={{ fontSize: 11, fill: "var(--muted-foreground)" }}
                  stroke="var(--border)"
                  minTickGap={48}
                />
                <YAxis
                  allowDecimals={false}
                  width={40}
                  tick={{ fontSize: 11, fill: "var(--muted-foreground)" }}
                  stroke="var(--border)"
                />
                <Tooltip
                  contentStyle={{
                    background: "var(--popover)",
                    border: "1px solid var(--border)",
                    borderRadius: 8,
                    fontSize: 12,
                    color: "var(--popover-foreground)",
                  }}
                  labelFormatter={(t) => new Date(Number(t) * 1000).toLocaleString()}
                />
                {SERIES.filter((s) => enabled[s.key]).map((s) => (
                  <Area
                    key={s.key}
                    type="monotone"
                    dataKey={s.key}
                    name={s.label}
                    stroke={s.color}
                    strokeWidth={2}
                    fill={`url(#g-${s.key})`}
                    isAnimationActive={false}
                    connectNulls
                    dot={false}
                  />
                ))}
              </AreaChart>
            </ResponsiveContainer>
          )}
          <p className="mt-3 text-xs text-muted-foreground">
            Message counts sampled from the broker every 15s (this Explorer session). Active = messages
            currently in the entity; Dead-lettered = messages in its dead-letter queue.
          </p>
        </CardContent>
      </Card>
    </div>
  );
}
