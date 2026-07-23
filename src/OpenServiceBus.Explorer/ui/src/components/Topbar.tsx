import { MoonIcon, RotateCcwIcon, SunIcon } from "lucide-react";
import { useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
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

export function Topbar() {
  const { status, selected, demoMode, resetIntervalSeconds } = useStore();
  const [dark, setDark] = useState(() => (localStorage.getItem(LS_THEME) ?? "light") === "dark");

  useEffect(() => {
    document.documentElement.classList.toggle("dark", dark);
    localStorage.setItem(LS_THEME, dark ? "dark" : "light");
  }, [dark]);

  const meta = STATUS_META[status];
  const KindIcon = selected ? KIND_ICON[selected.kind] : null;

  return (
    <header className="flex items-center gap-3 border-b bg-background px-4" style={{ gridArea: "topbar" }}>
      <div className="flex items-center gap-2.5 font-semibold">
        <BrandMark className="size-7" />
        <span className="tracking-tight">OpenServiceBus</span>
      </div>
      {selected && KindIcon && (
        <div className="flex min-w-0 items-center gap-2 text-sm text-muted-foreground">
          <span className="text-border">/</span>
          <KindIcon className="size-3.5" />
          <span className="truncate font-mono text-foreground">{displayName(selected)}</span>
        </div>
      )}
      <div className="ml-auto flex items-center gap-2">
        {demoMode && <ResetCountdown intervalSeconds={resetIntervalSeconds} />}
        <span className="flex items-center gap-2 rounded-full border px-3 py-1 text-xs text-muted-foreground">
          <span className={cn("size-2 rounded-full", meta.dot)} />
          {meta.label}
        </span>
        <Button variant="ghost" size="icon" onClick={() => setDark((d) => !d)} title="Toggle theme">
          {dark ? <SunIcon /> : <MoonIcon />}
        </Button>
      </div>
    </header>
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
