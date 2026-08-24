import { GlobeIcon, MenuIcon, MoonIcon, RefreshCwIcon, RotateCcwIcon, SunIcon } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { displayName } from "@/lib/format";
import { cn } from "@/lib/utils";
import { LS_THEME, useStore } from "@/store";
import { BrandMark } from "./BrandMark";
import { KIND_ICON } from "./kind";

const STATUS_META = {
  disconnected: { label: "disconnected", dot: "bg-muted-foreground/60" },
  pending: { label: "connecting…", dot: "bg-amber-500 animate-pulse" },
  ok: { label: "connected", dot: "bg-emerald-500" },
  err: { label: "error", dot: "bg-red-500" },
} as const;

export function Topbar({ onMenuClick }: { onMenuClick: () => void }) {
  const { status, selected, demoMode, resetIntervalSeconds, environments, activeEnvironment, setActiveEnvironment } = useStore();
  const [dark, setDark] = useState(() => (localStorage.getItem(LS_THEME) ?? "light") === "dark");

  useEffect(() => {
    document.documentElement.classList.toggle("dark", dark);
    localStorage.setItem(LS_THEME, dark ? "dark" : "light");
  }, [dark]);

  const meta = STATUS_META[status];
  const KindIcon = selected ? KIND_ICON[selected.kind] : null;

  return (
    <header className="flex h-[3.25rem] shrink-0 items-center gap-2 border-b bg-background px-3 sm:gap-3 sm:px-4">
      <Button
        variant="ghost"
        size="icon"
        className="-ml-1 shrink-0 md:hidden"
        onClick={onMenuClick}
        title="Toggle sidebar"
        aria-label="Toggle sidebar"
      >
        <MenuIcon />
      </Button>
      <div className="flex items-center gap-2.5 font-semibold">
        <BrandMark className="size-7 shrink-0" />
        <span className="hidden tracking-tight sm:inline">OpenServiceBus</span>
      </div>
      {selected && KindIcon && (
        <div className="hidden min-w-0 items-center gap-2 text-sm text-muted-foreground md:flex">
          <span className="text-border">/</span>
          <KindIcon className="size-3.5" />
          <span className="truncate font-mono text-foreground">{displayName(selected)}</span>
        </div>
      )}
      <div className="ml-auto flex items-center gap-1.5 sm:gap-2">
        {environments.length > 0 && (
          <Select
            value={activeEnvironment ?? "@none"}
            onValueChange={(v) => setActiveEnvironment(v === "@none" ? null : v)}
          >
            <SelectTrigger
              className="h-8 w-9 gap-1 px-2 text-xs sm:w-44 [&>svg:last-child]:hidden sm:[&>svg:last-child]:block"
              title="Active environment"
            >
              <GlobeIcon className="size-3.5 shrink-0 text-muted-foreground" />
              <span className="hidden min-w-0 truncate sm:block">
                <SelectValue placeholder="No environment" />
              </span>
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="@none">No environment</SelectItem>
              {environments.map((e) => (
                <SelectItem key={e.name} value={e.name}>{e.name}</SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
        {demoMode && <ResetCountdown intervalSeconds={resetIntervalSeconds} />}
        <RefreshInterval />
        <span className="flex items-center gap-2 rounded-full border px-2 py-1 text-xs text-muted-foreground sm:px-3">
          <span className={cn("size-2 rounded-full", meta.dot)} />
          <span className="hidden sm:inline">{meta.label}</span>
        </span>
        <Button variant="ghost" size="icon" onClick={() => setDark((d) => !d)} title="Toggle theme">
          {dark ? <SunIcon /> : <MoonIcon />}
        </Button>
      </div>
    </header>
  );
}

const REFRESH_OPTIONS = [
  { value: 0, label: "Off" },
  { value: 1, label: "1 sec" },
  { value: 3, label: "3 sec" },
  { value: 5, label: "5 sec" },
  { value: 15, label: "15 sec" },
  { value: 30, label: "30 sec" },
  { value: 60, label: "60 sec" },
] as const;

const LS_REFRESH = "osb-explorer-refresh-interval";

/**
 * Auto-refresh interval for the live counts. When on, it re-polls the entity lists every
 * N seconds via store.refresh(), which updates both the sidebar badges and the open
 * entity's header stats (they read from the same data). "Off" disables polling.
 */
function RefreshInterval() {
  const { refresh, status } = useStore();
  // Keep a ref to the latest refresh + status so the interval never needs to resubscribe
  // (and never captures a stale management connection or status).
  const stateRef = useRef({ refresh, status });
  stateRef.current = { refresh, status };

  const [seconds, setSeconds] = useState(() => {
    const v = Number(localStorage.getItem(LS_REFRESH));
    return REFRESH_OPTIONS.some((o) => o.value === v) ? v : 0;
  });

  useEffect(() => {
    localStorage.setItem(LS_REFRESH, String(seconds));
    if (!seconds) return;
    let inFlight = false;
    const id = window.setInterval(async () => {
      // Skip if a previous refresh is still running, or we're not connected (avoids
      // spamming error toasts every tick while disconnected).
      if (inFlight || stateRef.current.status !== "ok") return;
      inFlight = true;
      try {
        await stateRef.current.refresh();
      } finally {
        inFlight = false;
      }
    }, seconds * 1000);
    return () => window.clearInterval(id);
  }, [seconds]);

  return (
    <Select value={String(seconds)} onValueChange={(v) => setSeconds(Number(v))}>
      <SelectTrigger
        className="h-8 w-auto gap-1.5 px-2.5 text-xs"
        aria-label="Auto-refresh interval"
        title="Auto-refresh the open entity & sidebar counts"
      >
        <RefreshCwIcon className={cn("size-3.5", seconds > 0 ? "text-primary" : "text-muted-foreground")} />
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        {REFRESH_OPTIONS.map((o) => (
          <SelectItem key={o.value} value={String(o.value)}>
            {o.label}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}

/**
 * Countdown to the next demo reset. Resets happen on fixed wall-clock boundaries
 * (every intervalSeconds), so this and the seeder agree without any coordination.
 */
function ResetCountdown({ intervalSeconds }: { intervalSeconds: number }) {
  const [remaining, setRemaining] = useState(() => timeToNextBoundary(intervalSeconds));

  useEffect(() => {
    const id = window.setInterval(() => setRemaining(timeToNextBoundary(intervalSeconds)), 1000);
    return () => window.clearInterval(id);
  }, [intervalSeconds]);

  const mm = String(Math.floor(remaining / 60)).padStart(2, "0");
  const ss = String(remaining % 60).padStart(2, "0");
  const soon = remaining <= 60;

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span
          className={cn(
            "flex items-center gap-1.5 rounded-full border px-3 py-1 text-xs tabular-nums",
            soon ? "border-amber-500/40 text-amber-600 dark:text-amber-400" : "text-muted-foreground",
          )}
        >
          <RotateCcwIcon className={cn("size-3.5", soon && "animate-spin-slow")} />
          {mm}:{ss}
        </span>
      </TooltipTrigger>
      <TooltipContent>Demo resets every {Math.round(intervalSeconds / 60)} min · wipes & reseeds</TooltipContent>
    </Tooltip>
  );
}

function timeToNextBoundary(intervalSeconds: number): number {
  const now = Math.floor(Date.now() / 1000);
  const next = Math.ceil((now + 1) / intervalSeconds) * intervalSeconds;
  return Math.max(0, next - now);
}
