import { InboxIcon } from "lucide-react";
import { useState } from "react";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { humanTime, type Selected } from "@/lib/format";
import { dlqAddress, entityAddress, useStore } from "@/store";
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
      <main className="flex items-center justify-center overflow-y-auto p-6" style={{ gridArea: "main" }}>
        <div className="text-center text-muted-foreground">
          <InboxIcon className="mx-auto mb-3 size-10 opacity-40" />
          <h2 className="text-lg font-semibold text-foreground">Select an entity to get started</h2>
          <p className="mt-1 max-w-sm text-sm">
            Pick a queue, topic, or subscription from the sidebar - or create one with the Create button.
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

  return (
    <main className="min-h-0 space-y-4 overflow-y-auto p-5" style={{ gridArea: "main" }} key={address}>
      <Card>
        <CardContent className="pt-5">
          {!d && sel.kind !== "topic" ? (
            <div className="text-sm text-muted-foreground">Loading…</div>
          ) : (
            <>
              <div className="flex flex-wrap items-center gap-2">
                <h1 className="font-mono text-lg font-semibold">
                  {sel.kind === "subscription" ? `${sel.name} / ${sel.sub}` : sel.name}
                </h1>
                <Badge variant="secondary" className="capitalize">{sel.kind}</Badge>
                {d?.requiresSession && <Badge variant="success">Sessions</Badge>}
                {d?.requiresDuplicateDetection && <Badge variant="success">Dedup</Badge>}
                {d?.deadLetteringOnMessageExpiration && <Badge variant="warning">DLQ on TTL</Badge>}
                {d?.forwardTo && <Badge variant="outline">→ {d.forwardTo}</Badge>}
                {d?.forwardDeadLetteredMessagesTo && (
                  <Badge variant="outline">DLQ → {d.forwardDeadLetteredMessagesTo}</Badge>
                )}
              </div>
              <div className="mt-4 grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
                {sel.kind === "topic" ? (
                  <>
                    <Stat label="Subscriptions" value={String(subs.length)} />
                    <Stat
                      label="Dead-lettered"
                      value={String(subs.reduce((n, s) => n + (s.deadLetterMessageCount ?? 0), 0))}
                    />
                    <Stat label="Default TTL" value={humanTime(d?.defaultMessageTimeToLive)} />
                  </>
                ) : (
                  <>
                    <Stat label="Active messages" value={String(d?.activeMessageCount ?? "?")} />
                    <Stat label="Dead-lettered" value={String(d?.deadLetterMessageCount ?? "?")} />
                    <Stat label="Lock duration" value={humanTime(d?.lockDuration)} />
                    <Stat label="Max delivery" value={String(d?.maxDeliveryCount ?? "?")} />
                    <Stat label="Default TTL" value={humanTime(d?.defaultMessageTimeToLive)} />
                  </>
                )}
              </div>
            </>
          )}
        </CardContent>
      </Card>

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
      </Tabs>
    </main>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-lg border bg-muted/40 px-3 py-2">
      <div className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">{label}</div>
      <div className="mt-0.5 font-mono text-lg font-semibold">{value}</div>
    </div>
  );
}

export type { Selected };
