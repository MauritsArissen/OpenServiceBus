import {
  CableIcon, ChevronRightIcon, EraserIcon, InboxIcon, PlusIcon, RadioIcon, RefreshCwIcon,
  SearchIcon, WaypointsIcon, XIcon,
} from "lucide-react";
import { useState } from "react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Collapsible } from "@/components/ui/collapsible";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { explorerApi, type QueueInfo } from "@/lib/api";
import { cn } from "@/lib/utils";
import { useStore } from "@/store";

export function Sidebar({ open, onClose }: { open: boolean; onClose: () => void }) {
  const store = useStore();
  const [filter, setFilter] = useState("");
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [connOpen, setConnOpen] = useState(false);

  // Selecting an entity also dismisses the mobile drawer so the detail view is visible.
  // On desktop the drawer is always open, so onClose is a harmless no-op there.
  const selectAndClose = (sel: Parameters<typeof store.select>[0]) => {
    store.select(sel);
    onClose();
  };

  const f = filter.trim().toLowerCase();
  const queues = store.queues
    .filter((q) => !f || q.name.toLowerCase().includes(f))
    .sort((a, b) => a.name.localeCompare(b.name));
  const topics = store.topics
    .filter(
      (t) =>
        !f ||
        t.name.toLowerCase().includes(f) ||
        (store.subsByTopic[t.name] ?? []).some((s) => s.name.toLowerCase().includes(f)),
    )
    .sort((a, b) => a.name.localeCompare(b.name));

  const isSelected = (kind: string, name: string, sub?: string) =>
    store.selected?.kind === kind && store.selected.name === name && store.selected.sub === sub;

  return (
    <aside
      className={cn(
        "flex min-h-0 w-[300px] max-w-[85vw] flex-col border-r bg-sidebar text-sidebar-foreground",
        // Mobile: off-canvas drawer that slides in/out. Positioned absolute WITHIN the
        // content container (not viewport-fixed): iOS Safari tints its translucent bars
        // from viewport-anchored fixed elements, and this app never scrolls the document,
        // so a fixed drawer/scrim touching the bottom edge left the chrome tint stuck dark
        // after closing. Inside the container it never enters that heuristic.
        "absolute inset-y-0 left-0 z-40 transition-transform duration-200",
        open ? "translate-x-0" : "-translate-x-full",
        // Desktop (md+): static in-flow column, always visible.
        "md:static md:top-auto md:bottom-auto md:z-auto md:max-w-none md:translate-x-0 md:transition-none",
      )}
    >
      {/* Mobile-only drawer header with a close affordance (desktop hides this). */}
      <div className="flex items-center justify-between border-b px-3 py-2 md:hidden">
        <span className="text-sm font-semibold">Entities</span>
        <Button variant="ghost" size="icon-sm" onClick={onClose} aria-label="Close sidebar">
          <XIcon />
        </Button>
      </div>
      <div className="space-y-2.5 p-3">
        <div className="relative">
          <SearchIcon className="pointer-events-none absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
          <Input
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            placeholder="Filter entities…"
            className="h-9 border-transparent bg-black/[0.03] pl-8 shadow-none focus-visible:bg-transparent dark:bg-white/[0.04]"
          />
        </div>
        <div className="flex gap-2">
          <Button variant="outline" size="sm" className="flex-1" onClick={() => void store.refresh()}>
            <RefreshCwIcon className={cn(store.loading && "animate-spin")} /> Refresh
          </Button>
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button size="sm" className="flex-1">
                <PlusIcon /> Create
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              <DropdownMenuItem onClick={() => store.setDialog({ type: "createQueue" })}>
                <InboxIcon /> Queue
              </DropdownMenuItem>
              <DropdownMenuItem onClick={() => store.setDialog({ type: "createTopic" })}>
                <RadioIcon /> Topic
              </DropdownMenuItem>
              <DropdownMenuItem
                onClick={() => {
                  if (store.topics.length === 0) toast.error("Create a topic first.");
                  else store.setDialog({ type: "createSubscription" });
                }}
              >
                <WaypointsIcon /> Subscription
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
          <Tooltip>
            <TooltipTrigger asChild>
              <span>
                <Button
                  variant="outline"
                  size="sm"
                  className="px-2.5 text-muted-foreground hover:border-destructive/40 hover:text-destructive"
                  disabled={!store.purgeCapable}
                  aria-label="Purge all messages"
                  onClick={() =>
                    store.setDialog({
                      type: "confirm",
                      title: "Purge ALL messages?",
                      description:
                        "Every message on every queue, subscription, and dead-letter queue is removed. Entities and live consumers are untouched.",
                      destructive: true,
                      action: async () => {
                        const r = await explorerApi.purge(store.mgmt, {});
                        toast.success(`Purged ${r.purged} message(s) across ${r.entities ?? 0} entities`);
                        await store.refresh();
                      },
                    })
                  }
                >
                  <EraserIcon />
                </Button>
              </span>
            </TooltipTrigger>
            <TooltipContent>
              {store.purgeCapable
                ? "Purge all messages (keeps every entity)"
                : "Purge is OpenServiceBus-native and unavailable on this connection"}
            </TooltipContent>
          </Tooltip>
        </div>
      </div>

      <div className="min-h-0 flex-1 space-y-4 overflow-y-auto px-2 pb-3">
        <div>
          <SectionLabel label="Queues" count={queues.length} />
          {queues.length === 0 && <EmptyRow text={f ? "No matches" : "No queues yet"} />}
          {queues.map((q) => (
            <EntityRow
              key={q.name}
              icon={InboxIcon}
              name={q.name}
              active={isSelected("queue", q.name)}
              lockedCount={store.lockedCount(q.name) + store.lockedCount(q.name + "/$DeadLetterQueue")}
              count={q.activeMessageCount}
              onSelect={() => selectAndClose({ kind: "queue", name: q.name })}
            />
          ))}
        </div>

        <div>
          <SectionLabel label="Topics" count={topics.length} />
          {topics.length === 0 && <EmptyRow text={f ? "No matches" : "No topics yet"} />}
          {topics.map((t) => {
            const subs: QueueInfo[] = store.subsByTopic[t.name] ?? [];
            const open = expanded.has(t.name) || !!f;
            return (
              <div key={t.name}>
                <div
                  className={cn(
                    "group relative flex h-9 cursor-pointer items-center gap-1.5 rounded-md pl-1.5 pr-1.5 text-sm hover:bg-sidebar-accent",
                    isSelected("topic", t.name) && "bg-sidebar-accent font-medium",
                  )}
                  onClick={() => selectAndClose({ kind: "topic", name: t.name })}
                >
                  {isSelected("topic", t.name) && (
                    <span className="absolute inset-y-1.5 left-0 w-0.5 rounded-full bg-primary" />
                  )}
                  <button
                    className="grid size-5 shrink-0 place-items-center rounded hover:bg-sidebar-border"
                    onClick={(e) => {
                      e.stopPropagation();
                      setExpanded((s) => {
                        const next = new Set(s);
                        next.has(t.name) ? next.delete(t.name) : next.add(t.name);
                        return next;
                      });
                    }}
                  >
                    <ChevronRightIcon className={cn("size-3.5 transition-transform", open && "rotate-90")} />
                  </button>
                  <RadioIcon className="size-4 shrink-0 text-muted-foreground" />
                  <span className="min-w-0 flex-1 truncate font-mono text-[13px]">{t.name}</span>
                  <div className="flex items-center gap-1">
                    <span className="flex opacity-0 transition-opacity group-hover:opacity-100">
                      <Button
                        variant="ghost" size="icon-sm" title="Add subscription"
                        onClick={(e) => { e.stopPropagation(); store.setDialog({ type: "createSubscription", topic: t.name }); }}
                      >
                        <PlusIcon />
                      </Button>
                    </span>
                    <Badge variant="muted" className="tabular-nums">{subs.length}</Badge>
                  </div>
                </div>
                {open &&
                  subs
                    .filter((s) => !f || s.name.toLowerCase().includes(f) || t.name.toLowerCase().includes(f))
                    .map((s) => (
                      <EntityRow
                        key={s.name}
                        icon={WaypointsIcon}
                        name={s.name}
                        indent
                        active={isSelected("subscription", t.name, s.name)}
                        lockedCount={
                          store.lockedCount(`${t.name}/Subscriptions/${s.name}`) +
                          store.lockedCount(`${t.name}/Subscriptions/${s.name}/$DeadLetterQueue`)
                        }
                        count={s.activeMessageCount}
                        onSelect={() => selectAndClose({ kind: "subscription", name: t.name, sub: s.name })}
                      />
                    ))}
              </div>
            );
          })}
        </div>
      </div>

      <Collapsible
        open={connOpen}
        onOpenChange={setConnOpen}
        className="border-t p-3"
        trigger={
          <span className="flex items-center gap-2 text-xs font-medium text-muted-foreground">
            <CableIcon className="size-3.5" />
            Connection
            <span className={cn("ml-auto size-1.5 rounded-full", store.status === "ok" ? "bg-emerald-500" : store.status === "err" ? "bg-red-500" : "bg-muted-foreground/50")} />
            <ChevronRightIcon className={cn("size-3.5 transition-transform", connOpen && "rotate-90")} />
          </span>
        }
      >
        <div className="mt-3 space-y-2.5">
          <DemoLock locked={store.demoMode}>
            <div className="space-y-1">
              <Label>Connection string</Label>
              <Input
                value={store.conn}
                onChange={(e) => store.setConn(e.target.value)}
                disabled={store.demoMode}
                className="h-8 font-mono text-xs disabled:opacity-60"
              />
            </div>
            <div className="space-y-1">
              <Label>Management URL</Label>
              <Input
                value={store.mgmt}
                onChange={(e) => store.setMgmt(e.target.value)}
                disabled={store.demoMode}
                className="h-8 font-mono text-xs disabled:opacity-60"
              />
            </div>
          </DemoLock>
          {!store.demoMode && (
            <Button size="sm" className="w-full" onClick={() => void store.connect()}>
              Connect
            </Button>
          )}
          {store.pingResult && (
            <div className="space-y-0.5 rounded-md bg-muted/60 p-2 font-mono text-[11px] text-muted-foreground">
              <div>mgmt&nbsp;&nbsp;{store.pingResult.management}</div>
              <div>amqp&nbsp;&nbsp;{store.pingResult.serviceBus}</div>
              <div>atom&nbsp;&nbsp;{store.pingResult.atomManagement}</div>
            </div>
          )}
        </div>
      </Collapsible>

      {/* Running version - only shown when the image baked one in (OSB_EXPLORER_VERSION). */}
      {store.version && (
        <div className="border-t px-3 py-1.5 text-center text-[11px] text-muted-foreground">
          Explorer <span className="font-mono">v{store.version}</span>
        </div>
      )}
    </aside>
  );
}

/** In the hosted demo, wraps the (disabled) connection inputs so hovering the group
 *  shows why they can't be edited. A disabled input swallows hover, so the tooltip lives
 *  on this wrapper. */
function DemoLock({ locked, children }: { locked: boolean; children: React.ReactNode }) {
  if (!locked) return <div className="space-y-2.5">{children}</div>;
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <div className="cursor-not-allowed space-y-2.5">{children}</div>
      </TooltipTrigger>
      <TooltipContent>Not changeable in the live demo</TooltipContent>
    </Tooltip>
  );
}

function SectionLabel({ label, count }: { label: string; count: number }) {
  return (
    <div className="flex items-center gap-2 px-1.5 pb-1">
      <span className="text-[10px] font-semibold uppercase tracking-[0.08em] text-muted-foreground">{label}</span>
      <span className="text-[10px] font-medium tabular-nums text-muted-foreground/70">{count}</span>
    </div>
  );
}

function EmptyRow({ text }: { text: string }) {
  return <div className="px-2 py-1.5 text-xs text-muted-foreground">{text}</div>;
}

function EntityRow({
  icon: Icon, name, active, count, lockedCount, indent, onSelect,
}: {
  icon: typeof InboxIcon;
  name: string;
  active: boolean;
  count?: number;
  lockedCount: number;
  indent?: boolean;
  onSelect: () => void;
}) {
  return (
    <div
      className={cn(
        "group relative flex h-9 cursor-pointer items-center gap-2 rounded-md px-1.5 text-sm hover:bg-sidebar-accent",
        indent && "ml-5",
        active && "bg-sidebar-accent font-medium",
      )}
      onClick={onSelect}
    >
      {active && <span className="absolute inset-y-1.5 left-0 w-0.5 rounded-full bg-primary" />}
      <Icon className={cn("size-4 shrink-0", active ? "text-primary" : "text-muted-foreground")} />
      <span className="min-w-0 flex-1 truncate font-mono text-[13px]">{name}</span>
      {lockedCount > 0 && <Badge variant="warning" className="tabular-nums">{lockedCount}🔒</Badge>}
      <Badge variant="muted" className="tabular-nums">{count ?? "?"}</Badge>
    </div>
  );
}
