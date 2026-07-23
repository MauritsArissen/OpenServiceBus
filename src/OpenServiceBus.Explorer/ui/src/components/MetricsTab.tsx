import { ActivityIcon } from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";
import {
  Area, AreaChart, Bar, BarChart, CartesianGrid, Legend, ResponsiveContainer, Tooltip, XAxis, YAxis,
} from "recharts";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { explorerApi, type MetricSample } from "@/lib/api";
import { type Selected } from "@/lib/format";
import { bucketize, type Bucket } from "@/lib/metrics";
import { cn } from "@/lib/utils";
import { entityAddress } from "@/store";

const WINDOWS = [
  { label: "30 min", seconds: 1800 },
  { label: "1 hour", seconds: 3600 },
  { label: "4 hours", seconds: 14400 },
  { label: "12 hours", seconds: 43200 },
  { label: "24 hours", seconds: 86400 },
];

const FLOW = [
  { key: "newMessages", label: "New", color: "var(--color-brand)" },
  { key: "completed", label: "Completed", color: "oklch(0.68 0.15 155)" },
  { key: "deadLettered", label: "Dead-lettered", color: "oklch(0.62 0.2 25)" },
] as const;

const LEVELS = [
  { key: "active", label: "Active", color: "var(--color-brand)" },
  { key: "dlqActive", label: "Dead-lettered", color: "oklch(0.62 0.2 25)" },
] as const;

export function MetricsTab({ sel }: { sel: Selected }) {
  const address = entityAddress(sel);
  const [windowSeconds, setWindowSeconds] = useState(1800);
  const [flowOn, setFlowOn] = useState<Record<string, boolean>>({ newMessages: true, completed: true, deadLettered: true });
  const [levelOn, setLevelOn] = useState<Record<string, boolean>>({ active: true, dlqActive: true });
  const [raw, setRaw] = useState<{ entity: MetricSample[]; dlq: MetricSample[] } | null>(null);
  const timer = useRef<number | null>(null);

  useEffect(() => {
    let cancelled = false;
    const load = async () => {
      try {
        const r = await explorerApi.metrics(address, windowSeconds);
        if (!cancelled) setRaw(r);
      } catch {
        if (!cancelled) setRaw({ entity: [], dlq: [] });
      }
    };
    void load();
    timer.current = window.setInterval(load, 15000);
    return () => {
      cancelled = true;
      if (timer.current) window.clearInterval(timer.current);
    };
  }, [address, windowSeconds]);

  const buckets = useMemo<Bucket[]>(
    () => (raw ? bucketize(raw.entity, raw.dlq, windowSeconds, Math.floor(Date.now() / 1000)) : []),
    [raw, windowSeconds],
  );

  const fmtTime = (t: number) =>
    new Date(t * 1000).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", hour12: false });
  const tooltipLabel = (t: number) => new Date(Number(t) * 1000).toLocaleString();
  const tooltipStyle = {
    background: "var(--popover)",
    border: "1px solid var(--border)",
    borderRadius: 8,
    fontSize: 12,
    color: "var(--popover-foreground)",
  };
  const axisTick = { fontSize: 11, fill: "var(--muted-foreground)" };
  const windowLabel = WINDOWS.find((w) => w.seconds === windowSeconds)?.label;

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <Select value={String(windowSeconds)} onValueChange={(v) => setWindowSeconds(Number(v))}>
          <SelectTrigger className="w-36"><SelectValue /></SelectTrigger>
          <SelectContent>
            {WINDOWS.map((w) => (
              <SelectItem key={w.seconds} value={String(w.seconds)}>Last {w.label}</SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>

      {/* Throughput - messages per interval */}
      <Card>
        <CardHeader className="flex-row items-center justify-between space-y-0 pb-2">
          <CardTitle className="text-sm">Throughput <span className="font-normal text-muted-foreground">· per interval</span></CardTitle>
          <Toggles items={FLOW} on={flowOn} setOn={setFlowOn} />
        </CardHeader>
        <CardContent>
          <ResponsiveContainer width="100%" height={220}>
            <BarChart data={buckets} margin={{ top: 4, right: 8, left: -14, bottom: 0 }}>
              <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" vertical={false} />
              <XAxis dataKey="t" tickFormatter={fmtTime} tick={axisTick} stroke="var(--border)" minTickGap={44} />
              <YAxis allowDecimals={false} width={42} tick={axisTick} stroke="var(--border)" />
              <Tooltip contentStyle={tooltipStyle} labelFormatter={tooltipLabel} cursor={{ fill: "var(--muted)", opacity: 0.4 }} />
              <Legend wrapperStyle={{ fontSize: 11 }} />
              {FLOW.filter((s) => flowOn[s.key]).map((s) => (
                <Bar key={s.key} dataKey={s.key} name={s.label} fill={s.color} radius={[2, 2, 0, 0]} isAnimationActive={false} stackId="flow" />
              ))}
            </BarChart>
          </ResponsiveContainer>
        </CardContent>
      </Card>

      {/* Levels - counts at each moment */}
      <Card>
        <CardHeader className="flex-row items-center justify-between space-y-0 pb-2">
          <CardTitle className="text-sm">Message counts <span className="font-normal text-muted-foreground">· at each moment</span></CardTitle>
          <Toggles items={LEVELS} on={levelOn} setOn={setLevelOn} />
        </CardHeader>
        <CardContent>
          {buckets.length === 0 ? (
            <div className="flex h-[220px] flex-col items-center justify-center text-center text-sm text-muted-foreground">
              <ActivityIcon className="mb-3 size-8 opacity-40" />
              <p className="font-medium text-foreground">Collecting metrics…</p>
              <p className="mt-1 max-w-sm">Sampled every 15s; the {windowLabel} window fills in over time.</p>
            </div>
          ) : (
            <ResponsiveContainer width="100%" height={220}>
              <AreaChart data={buckets} margin={{ top: 4, right: 8, left: -14, bottom: 0 }}>
                <defs>
                  {LEVELS.map((s) => (
                    <linearGradient key={s.key} id={`lv-${s.key}`} x1="0" y1="0" x2="0" y2="1">
                      <stop offset="0%" stopColor={s.color} stopOpacity={0.35} />
                      <stop offset="100%" stopColor={s.color} stopOpacity={0.02} />
                    </linearGradient>
                  ))}
                </defs>
                <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" vertical={false} />
                <XAxis dataKey="t" tickFormatter={fmtTime} tick={axisTick} stroke="var(--border)" minTickGap={44} />
                <YAxis allowDecimals={false} width={42} tick={axisTick} stroke="var(--border)" />
                <Tooltip contentStyle={tooltipStyle} labelFormatter={tooltipLabel} />
                <Legend wrapperStyle={{ fontSize: 11 }} />
                {LEVELS.filter((s) => levelOn[s.key]).map((s) => (
                  <Area
                    key={s.key} type="monotone" dataKey={s.key} name={s.label}
                    stroke={s.color} strokeWidth={2} fill={`url(#lv-${s.key})`}
                    isAnimationActive={false} dot={false}
                  />
                ))}
              </AreaChart>
            </ResponsiveContainer>
          )}
        </CardContent>
      </Card>

      <p className="text-xs text-muted-foreground">
        Sampled from the broker every 15s for this Explorer session. Throughput bars show messages that
        arrived / completed / were dead-lettered in each interval; the lower chart shows how many messages
        are in the entity and its dead-letter queue at each moment.
      </p>
    </div>
  );
}

function Toggles<T extends { key: string; label: string; color: string }>({
  items, on, setOn,
}: {
  items: readonly T[];
  on: Record<string, boolean>;
  setOn: React.Dispatch<React.SetStateAction<Record<string, boolean>>>;
}) {
  return (
    <div className="flex gap-1.5">
      {items.map((s) => (
        <Button
          key={s.key}
          variant="outline"
          size="sm"
          onClick={() => setOn((e) => ({ ...e, [s.key]: !e[s.key] }))}
          className={cn("h-7 gap-1.5 px-2 text-xs", !on[s.key] && "opacity-45")}
        >
          <span className="size-2.5 rounded-[3px]" style={{ background: s.color }} />
          {s.label}
        </Button>
      ))}
    </div>
  );
}
