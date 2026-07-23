import { MoonIcon, SunIcon } from "lucide-react";
import { useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { displayName } from "@/lib/format";
import { LS_THEME, useStore } from "@/store";

const STATUS_META = {
  disconnected: { label: "disconnected", dot: "bg-muted-foreground" },
  pending: { label: "connecting…", dot: "bg-amber-500 animate-pulse" },
  ok: { label: "connected", dot: "bg-emerald-500" },
  err: { label: "error", dot: "bg-red-500" },
} as const;

export function Topbar() {
  const { status, selected } = useStore();
  const [dark, setDark] = useState(() => (localStorage.getItem(LS_THEME) ?? "light") === "dark");

  useEffect(() => {
    document.documentElement.classList.toggle("dark", dark);
    localStorage.setItem(LS_THEME, dark ? "dark" : "light");
  }, [dark]);

  const meta = STATUS_META[status];

  return (
    <header className="flex h-13 items-center gap-3 border-b bg-background px-4" style={{ gridArea: "topbar" }}>
      <div className="flex items-center gap-2.5 font-semibold">
        <span className="flex size-7 items-center justify-center rounded-lg bg-gradient-to-br from-indigo-500 to-fuchsia-500 text-sm text-white shadow-sm">
          ◆
        </span>
        OpenServiceBus
      </div>
      {selected && (
        <div className="flex min-w-0 items-center gap-2 text-sm text-muted-foreground">
          <span>/</span>
          <span className="truncate font-mono text-foreground">{displayName(selected)}</span>
        </div>
      )}
      <div className="ml-auto flex items-center gap-2">
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
