import {
  ClockIcon, InboxIcon, LayersIcon, LockIcon, RepeatIcon, SkullIcon, Trash2Icon, WaypointsIcon,
  type LucideIcon,
} from "lucide-react";
import { useState } from "react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { explorerApi } from "@/lib/api";
import { humanTime, type Selected } from "@/lib/format";
import { cn } from "@/lib/utils";
import { dlqAddress, entityAddress, useStore } from "@/store";
import { KIND_ICON, KIND_TILE } from "./kind";
import { MetricsTab } from "./MetricsTab";
import { OverviewTab } from "./OverviewTab";
import { ReceiveTab } from "./ReceiveTab";
import { RulesTab } from "./RulesTab";
import { SendTab } from "./SendTab";

export function EntityView() {
  const store = useStore();
  const [tab, setTab] = useState("overview");
  const sel = store.selected;

  if (!sel) {
    return (
      <main className="flex min-w-0 flex-1 items-center justify-center overflow-y-auto bg-muted/30 p-6">
        <div className="max-w-sm text-center">
          <div className="mx-auto mb-4 grid size-14 place-items-center rounded-2xl bg-primary/10 text-primary">
            <LayersIcon className="size-7" />
          </div>
          <h2 className="text-lg font-semibold">Select an entity to get started</h2>
          <p className="mt-1.5 text-sm text-muted-foreground">
            Pick a queue, topic, or subscription from the sidebar - or spin one up with the Create button.
          </p>
        </div>
      </main>
    );
  }

  const d = store.descriptorFor(sel);
  const address = entityAddress(sel);
  const dlq = dlqAddress(sel);
  const hasDlq = sel.kind !== "topic";
  const canSend = sel.kind === "queue" || sel.kind === "topic";
  const canReceive = sel.kind !== "topic";
  const lockedMain = store.lockedCount(address);
  const dlqBadge = store.lockedCount(dlq) || d?.deadLetterMessageCount || 0;
  const subs = sel.kind === "topic" ? store.subsByTopic[sel.name] ?? [] : [];
  const KindIcon = KIND_ICON[sel.kind];

  // Delete the selected entity from its own card - an explicit, always-visible button that
  // works on touch (the sidebar's hover-only trash icon was invisible on phones and easy to
  // tap by accident). The confirm dialog guards the destructive action.
  const handleDelete = () => {
    const confirm = (title: string, description: string, action: () => Promise<void>) =>
      store.setDialog({ type: "confirm", title, description, destructive: true, action });

    if (sel.kind === "queue") {
      confirm(`Delete queue '${sel.name}'?`, "The queue, its dead-letter queue, and all messages are removed.", async () => {
        await explorerApi.deleteQueue(store.mgmt, sel.name);
        store.clearEntityLocal(sel.name);
        store.select(null);
        toast.success(`Deleted queue '${sel.name}'`);
        await store.refresh();
      });
    } else if (sel.kind === "topic") {
      confirm(`Delete topic '${sel.name}'?`, "The topic and ALL its subscriptions are removed.", async () => {
        await explorerApi.deleteTopic(store.mgmt, sel.name);
        store.select(null);
        toast.success(`Deleted topic '${sel.name}'`);
        await store.refresh();
      });
    } else {
      confirm(`Delete subscription '${sel.name}/${sel.sub}'?`, "The subscription and its messages are removed.", async () => {
        await explorerApi.deleteSubscription(store.mgmt, sel.name, sel.sub!);
        store.clearEntityLocal(`${sel.name}/Subscriptions/${sel.sub}`);
        store.select(null);
        toast.success(`Deleted subscription '${sel.name}/${sel.sub}'`);
        await store.refresh();
      });
    }
  };

  return (
    <main className="min-h-0 min-w-0 flex-1 overflow-y-auto bg-muted/30" key={address}>
      <div className="mx-auto max-w-5xl space-y-5 p-5">
        <div className="rounded-xl border bg-card p-5 shadow-sm">
          {!d && sel.kind !== "topic" ? (
            <div className="text-sm text-muted-foreground">Loading…</div>
          ) : (
            <>
              <div className="flex items-start gap-3.5">
                <div className={cn("grid size-11 shrink-0 place-items-center rounded-xl", KIND_TILE[sel.kind])}>
                  <KindIcon className="size-5.5" />
                </div>
                <div className="min-w-0 flex-1">
                  <h1 className="truncate font-mono text-lg font-semibold leading-tight">
                    {sel.kind === "subscription" ? `${sel.name} / ${sel.sub}` : sel.name}
                  </h1>
                  <div className="mt-1.5 flex flex-wrap items-center gap-1.5">
                    <Badge variant="secondary" className="capitalize">{sel.kind}</Badge>
                    {d?.requiresSession && <Badge variant="success">Sessions</Badge>}
                    {d?.requiresDuplicateDetection && <Badge variant="success">Dedup</Badge>}
                    {d?.deadLetteringOnMessageExpiration && <Badge variant="warning">DLQ on TTL</Badge>}
                    {d?.forwardTo && <Badge variant="outline">→ {d.forwardTo}</Badge>}
                    {d?.forwardDeadLetteredMessagesTo && (
                      <Badge variant="outline">DLQ → {d.forwardDeadLetteredMessagesTo}</Badge>
                    )}
                  </div>
                </div>
                <Button
                  variant="outline"
                  size="sm"
                  className="shrink-0 text-muted-foreground hover:border-destructive/40 hover:text-destructive"
                  onClick={handleDelete}
                >
                  <Trash2Icon /> Delete
                </Button>
              </div>
              <div className="mt-5 grid grid-cols-2 gap-2.5 sm:grid-cols-3 lg:grid-cols-5">
                {sel.kind === "topic" ? (
                  <>
                    <Stat icon={WaypointsIcon} label="Subscriptions" value={String(subs.length)} />
                    <Stat icon={SkullIcon} label="Dead-lettered" value={String(subs.reduce((n, s) => n + store.dlqCount(`${sel.name}/Subscriptions/${s.name}`), 0))} />
                    <Stat icon={ClockIcon} label="Default TTL" value={humanTime(d?.defaultMessageTimeToLive)} />
                  </>
                ) : (
                  <>
                    <Stat icon={InboxIcon} label="Active" value={String(d?.activeMessageCount ?? "?")} accent />
                    <Stat icon={SkullIcon} label="Dead-lettered" value={String(store.dlqCount(address))} />
                    <Stat icon={LockIcon} label="Lock duration" value={humanTime(d?.lockDuration)} />
                    <Stat icon={RepeatIcon} label="Max delivery" value={String(d?.maxDeliveryCount ?? "?")} />
                    <Stat icon={ClockIcon} label="Default TTL" value={humanTime(d?.defaultMessageTimeToLive)} />
                  </>
                )}
              </div>
            </>
          )}
        </div>

        <Tabs value={tab} onValueChange={setTab}>
          <TabsList>
            <TabsTrigger value="overview">Overview</TabsTrigger>
            <TabsTrigger value="send" disabled={!canSend}>Send</TabsTrigger>
            <TabsTrigger value="receive" disabled={!canReceive}>
              Receive {lockedMain > 0 && <Badge variant="warning">{lockedMain}</Badge>}
            </TabsTrigger>
            {hasDlq && (
              <TabsTrigger value="deadletter">
                Dead-letter {dlqBadge > 0 && <Badge variant="destructive">{dlqBadge}</Badge>}
              </TabsTrigger>
            )}
            {sel.kind === "subscription" && <TabsTrigger value="rules">Rules</TabsTrigger>}
            <TabsTrigger value="metrics">Metrics</TabsTrigger>
          </TabsList>

          <TabsContent value="overview">
            <OverviewTab sel={sel} onGoto={setTab} />
          </TabsContent>
          {canSend && (
            <TabsContent value="send">
              <SendTab sel={sel} />
            </TabsContent>
          )}
          {canReceive && (
            <TabsContent value="receive">
              <ReceiveTab sel={sel} target={address} />
            </TabsContent>
          )}
          {hasDlq && (
            <TabsContent value="deadletter">
              <ReceiveTab sel={sel} target={dlq} isDlq />
            </TabsContent>
          )}
          {sel.kind === "subscription" && (
            <TabsContent value="rules">
              <RulesTab topic={sel.name} sub={sel.sub!} />
            </TabsContent>
          )}
          <TabsContent value="metrics">
            <MetricsTab sel={sel} />
          </TabsContent>
        </Tabs>
      </div>
    </main>
  );
}

function Stat({ icon: Icon, label, value, accent }: { icon: LucideIcon; label: string; value: string; accent?: boolean }) {
  return (
    <div className="rounded-lg border bg-background px-3 py-2.5">
      <div className="flex items-center gap-1.5 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
        <Icon className="size-3.5" />
        {label}
      </div>
      <div className={cn("mt-1 font-mono text-xl font-semibold tabular-nums", accent && "text-primary")}>{value}</div>
    </div>
  );
}

export type { Selected };
