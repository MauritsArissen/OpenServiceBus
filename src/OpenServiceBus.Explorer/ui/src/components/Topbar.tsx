import { CheckIcon, GlobeIcon, MenuIcon, MoonIcon, RefreshCwIcon, RotateCcwIcon, SunIcon } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
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
      <div className="ml-auto flex items-center gap-1 sm:gap-1.5">
        {demoMode && <ResetCountdown intervalSeconds={resetIntervalSeconds} />}
        {environments.length > 0 && (
          <EnvironmentSwitcher
            environments={environments.map((e) => e.name)}
            active={activeEnvironment}
            onChange={setActiveEnvironment}
          />
        )}
        <RefreshInterval />
        <Tooltip>
          <TooltipTrigger asChild>
            <span className="flex h-8 items-center gap-2 rounded-full border px-2.5 sm:px-3">
              <span className={cn("size-2 rounded-full", meta.dot)} />
              <span className="hidden text-xs text-muted-foreground lg:inline">{meta.label}</span>
            </span>
          </TooltipTrigger>
          <TooltipContent>Broker {meta.label}</TooltipContent>
        </Tooltip>
        <Button
          variant="ghost"
          size="icon"
          className="size-8 rounded-full"
          onClick={() => setDark((d) => !d)}
          title="Toggle theme"
        >
          {dark ? <SunIcon /> : <MoonIcon />}
        </Button>
      </div>
    </header>
  );
}

/** Active-environment switcher: a pill that carries the environment accent color when
 *  one is active, opening a menu instead of a form-style select. Icon-only on phones. */
function EnvironmentSwitcher({
  environments, active, onChange,
}: {
  environments: string[];
  active: string | null;
  onChange: (name: string | null) => void;
}) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          className={cn(
            "flex h-8 max-w-44 items-center gap-1.5 rounded-full border px-2.5 text-xs transition-colors sm:px-3",
            active
              ? "border-sky-500/40 bg-sky-500/10 text-sky-600 hover:bg-sky-500/15 dark:text-sky-400"
              : "text-muted-foreground hover:bg-accent",
          )}
          title="Active environment"
          aria-label="Active environment"
        >
          <GlobeIcon className="size-3.5 shrink-0" />
          <span className="hidden min-w-0 truncate font-medium sm:block">
            {active ?? "No environment"}
          </span>
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="min-w-48">
        <DropdownMenuLabel>Active environment</DropdownMenuLabel>
        <DropdownMenuItem onClick={() => onChange(null)}>
          <CheckIcon className={cn("size-3.5", active !== null && "invisible")} />
          No environment
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        {environments.map((name) => (
          <DropdownMenuItem key={name} onClick={() => onChange(name)}>
            <CheckIcon className={cn("size-3.5", active !== name && "invisible")} />
            <span className="truncate">{name}</span>
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
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
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          className={cn(
            "flex h-8 items-center gap-1.5 rounded-full border px-2.5 text-xs transition-colors hover:bg-accent sm:px-3",
            seconds > 0 ? "text-primary" : "text-muted-foreground",
          )}
          aria-label="Auto-refresh interval"
          title="Auto-refresh the open entity & sidebar counts"
        >
          <RefreshCwIcon className="size-3.5 shrink-0" />
          {seconds > 0 && <span className="hidden font-medium tabular-nums sm:inline">{seconds}s</span>}
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="min-w-32">
        <DropdownMenuLabel>Auto-refresh</DropdownMenuLabel>
        {REFRESH_OPTIONS.map((o) => (
          <DropdownMenuItem key={o.value} onClick={() => setSeconds(o.value)}>
            <CheckIcon className={cn("size-3.5", seconds !== o.value && "invisible")} />
            {o.label}
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
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
            "flex h-8 items-center gap-1.5 rounded-full border px-2.5 text-xs tabular-nums sm:px-3",
            soon ? "border-amber-500/40 bg-amber-500/10 text-amber-600 dark:text-amber-400" : "text-muted-foreground",
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
