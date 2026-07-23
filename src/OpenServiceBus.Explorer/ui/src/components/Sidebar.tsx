import {
  CableIcon, ChevronRightIcon, InboxIcon, ListTreeIcon, LockIcon,
  PlusIcon, RefreshCwIcon, SearchIcon, Trash2Icon,
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
import { Separator } from "@/components/ui/separator";
import { explorerApi, type QueueInfo } from "@/lib/api";
import { cn } from "@/lib/utils";
import { useStore } from "@/store";

export function Sidebar() {
  const store = useStore();
  const [filter, setFilter] = useState("");
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [connOpen, setConnOpen] = useState(false);

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

  const confirmDelete = (title: string, description: string, action: () => Promise<void>) =>
    store.setDialog({ type: "confirm", title, description, destructive: true, action });

  const deleteQueue = (name: string) =>
    confirmDelete(`Delete queue '${name}'?`, "The queue, its dead-letter queue, and all messages are removed.", async () => {
      await explorerApi.deleteQueue(store.mgmt, name);
      store.clearEntityLocal(name);
      if (isSelected("queue", name)) store.select(null);
      toast.success(`Deleted queue '${name}'`);
      await store.refresh();
    });

  const deleteTopic = (name: string) =>
    confirmDelete(`Delete topic '${name}'?`, "The topic and ALL its subscriptions are removed.", async () => {
      await explorerApi.deleteTopic(store.mgmt, name);
      if (store.selected?.name === name) store.select(null);
      toast.success(`Deleted topic '${name}'`);
      await store.refresh();
    });

  const deleteSubscription = (topic: string, sub: string) =>
    confirmDelete(`Delete subscription '${topic}/${sub}'?`, "The subscription and its messages are removed.", async () => {
      await explorerApi.deleteSubscription(store.mgmt, topic, sub);
      store.clearEntityLocal(`${topic}/Subscriptions/${sub}`);
      if (isSelected("subscription", topic, sub)) store.select(null);
      toast.success(`Deleted subscription '${topic}/${sub}'`);
      await store.refresh();
    });

  return (
    <aside
      className="flex min-h-0 flex-col border-r bg-sidebar text-sidebar-foreground"
      style={{ gridArea: "sidebar" }}
    >
      <div className="space-y-2 p-3">
        <div className="relative">
          <SearchIcon className="absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
          <Input
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            placeholder="Filter entities…"
            className="h-8 pl-8"
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
                <ListTreeIcon /> Topic
              </DropdownMenuItem>
              <DropdownMenuItem
                onClick={() => {
                  if (store.topics.length === 0) toast.error("Create a topic first.");
                  else store.setDialog({ type: "createSubscription" });
                }}
              >
                <ChevronRightIcon /> Subscription
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
      </div>

      <Separator className="bg-sidebar-border" />

      <div className="min-h-0 flex-1 overflow-y-auto p-2">
        <SectionLabel label="Queues" count={queues.length} />
        {queues.length === 0 && <EmptyRow text={f ? "No matches" : "No queues yet"} />}
        {queues.map((q) => (
          <EntityRow
            key={q.name}
            name={q.name}
            active={isSelected("queue", q.name)}
            lockedCount={store.lockedCount(q.name) + store.lockedCount(q.name + "/$DeadLetterQueue")}
            count={q.activeMessageCount}
            onSelect={() => store.select({ kind: "queue", name: q.name })}
            onDelete={() => deleteQueue(q.name)}
          />
        ))}

        <SectionLabel label="Topics" count={topics.length} className="mt-3" />
        {topics.length === 0 && <EmptyRow text={f ? "No matches" : "No topics yet"} />}
        {topics.map((t) => {
          const subs: QueueInfo[] = store.subsByTopic[t.name] ?? [];
          const open = expanded.has(t.name) || !!f;
          return (
            <div key={t.name}>
              <div
                className={cn(
                  "group flex cursor-pointer items-center gap-1 rounded-md px-1.5 py-1.5 text-sm hover:bg-sidebar-accent",
                  isSelected("topic", t.name) && "bg-sidebar-accent",
                )}
                onClick={() => store.select({ kind: "topic", name: t.name })}
              >
                <button
                  className="cursor-pointer rounded p-0.5 hover:bg-sidebar-border"
                  onClick={(e) => {
                    e.stopPropagation();
                    setExpanded((s) => {
                      const next = new Set(s);
                      if (next.has(t.name)) next.delete(t.name);
                      else next.add(t.name);
                      return next;
                    });
                  }}
                >
                  <ChevronRightIcon className={cn("size-3.5 transition-transform", open && "rotate-90")} />
                </button>
                <span className="min-w-0 flex-1 truncate font-mono text-[13px]">{t.name}</span>
                <span className="hidden gap-0.5 group-hover:flex">
                  <Button
                    variant="ghost" size="icon-sm" title="Add subscription"
                    onClick={(e) => { e.stopPropagation(); store.setDialog({ type: "createSubscription", topic: t.name }); }}
                  >
                    <PlusIcon />
                  </Button>
                  <Button
                    variant="ghost" size="icon-sm" title="Delete topic"
                    className="text-destructive"
                    onClick={(e) => { e.stopPropagation(); deleteTopic(t.name); }}
                  >
                    <Trash2Icon />
                  </Button>
                </span>
                <Badge variant="muted">{subs.length}</Badge>
              </div>
              {open &&
                subs
                  .filter((s) => !f || s.name.toLowerCase().includes(f) || t.name.toLowerCase().includes(f))
                  .map((s) => (
                    <EntityRow
                      key={s.name}
                      name={s.name}
                      indent
                      active={isSelected("subscription", t.name, s.name)}
                      lockedCount={
                        store.lockedCount(`${t.name}/Subscriptions/${s.name}`) +
                        store.lockedCount(`${t.name}/Subscriptions/${s.name}/$DeadLetterQueue`)
                      }
                      count={s.activeMessageCount}
                      onSelect={() => store.select({ kind: "subscription", name: t.name, sub: s.name })}
                      onDelete={() => deleteSubscription(t.name, s.name)}
                    />
                  ))}
            </div>
          );
        })}
      </div>

      <Separator className="bg-sidebar-border" />

      <Collapsible
        open={connOpen}
        onOpenChange={setConnOpen}
        className="p-3"
        trigger={
          <span className="flex items-center gap-2 text-xs font-medium text-muted-foreground">
            <CableIcon className="size-3.5" />
            Connection
            <ChevronRightIcon className={cn("ml-auto size-3.5 transition-transform", connOpen && "rotate-90")} />
          </span>
        }
      >
        <div className="mt-3 space-y-2">
          <div className="space-y-1">
            <Label>Connection string</Label>
            <Input value={store.conn} onChange={(e) => store.setConn(e.target.value)} className="h-8 font-mono text-xs" />
          </div>
          <div className="space-y-1">
            <Label>Management URL</Label>
            <Input value={store.mgmt} onChange={(e) => store.setMgmt(e.target.value)} className="h-8 font-mono text-xs" />
          </div>
          <Button size="sm" className="w-full" onClick={() => void store.connect()}>
            Connect
          </Button>
          {store.pingResult && (
            <div className="space-y-0.5 font-mono text-[11px] text-muted-foreground">
              <div>mgmt&nbsp;&nbsp;{store.pingResult.management}</div>
              <div>amqp&nbsp;&nbsp;{store.pingResult.serviceBus}</div>
            </div>
          )}
        </div>
      </Collapsible>
    </aside>
  );
}

function SectionLabel({ label, count, className }: { label: string; count: number; className?: string }) {
  return (
    <div className={cn("flex items-center justify-between px-1.5 pb-1 pt-2", className)}>
      <span className="text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">{label}</span>
      <Badge variant="muted">{count}</Badge>
    </div>
  );
}

function EmptyRow({ text }: { text: string }) {
  return <div className="px-2 py-1.5 text-xs text-muted-foreground">{text}</div>;
}

function EntityRow({
  name, active, count, lockedCount, indent, onSelect, onDelete,
}: {
  name: string;
  active: boolean;
  count?: number;
  lockedCount: number;
  indent?: boolean;
  onSelect: () => void;
  onDelete: () => void;
}) {
  return (
    <div
      className={cn(
        "group flex cursor-pointer items-center gap-1.5 rounded-md px-1.5 py-1.5 text-sm hover:bg-sidebar-accent",
        indent && "ml-5",
        active && "bg-sidebar-accent",
      )}
      onClick={onSelect}
    >
      <span className="min-w-0 flex-1 truncate font-mono text-[13px]">{name}</span>
      <Button
        variant="ghost" size="icon-sm" title="Delete"
        className="hidden text-destructive group-hover:inline-flex"
        onClick={(e) => { e.stopPropagation(); onDelete(); }}
      >
        <Trash2Icon />
      </Button>
      {lockedCount > 0 && (
        <Badge variant="warning">
          {lockedCount} <LockIcon className="size-2.5" />
        </Badge>
      )}
      <Badge variant="muted">{count ?? "?"}</Badge>
    </div>
  );
}
