import { InboxIcon, SendIcon, SlidersHorizontalIcon } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { humanTime, type Selected } from "@/lib/format";
import { useStore } from "@/store";

export function OverviewTab({ sel, onGoto }: { sel: Selected; onGoto: (tab: string) => void }) {
  const store = useStore();
  const d = store.descriptorFor(sel);
  const subs = sel.kind === "topic" ? store.subsByTopic[sel.name] ?? [] : [];

  const rows: [string, string][] =
    sel.kind === "topic"
      ? [
          ["Name", sel.name],
          ["Subscriptions", String(subs.length)],
          ["Dead-lettered", String(subs.reduce((n, s) => n + (s.deadLetterMessageCount ?? 0), 0))],
          ["Default TTL", d?.defaultMessageTimeToLive ? humanTime(d.defaultMessageTimeToLive) : "∞ (unlimited)"],
        ]
      : [
          ["Name", sel.kind === "subscription" ? `${sel.name}/${sel.sub}` : sel.name],
          ...(sel.kind === "subscription" ? [["Topic", sel.name] as [string, string]] : []),
          ["Active messages", String(d?.activeMessageCount ?? "?")],
          ["Dead-lettered", String(d?.deadLetterMessageCount ?? "?")],
          ["Lock duration", humanTime(d?.lockDuration)],
          ["Max delivery count", String(d?.maxDeliveryCount ?? "?")],
          ["Default TTL", d?.defaultMessageTimeToLive ? humanTime(d.defaultMessageTimeToLive) : "∞ (unlimited)"],
          ["DLQ on expiration", d?.deadLetteringOnMessageExpiration ? "yes" : "no"],
          ["Sessions required", d?.requiresSession ? "yes" : "no"],
          [
            "Duplicate detection",
            d?.requiresDuplicateDetection
              ? `yes · window ${humanTime(d.duplicateDetectionHistoryTimeWindow)}`
              : "no",
          ],
          ["Auto-forward to", d?.forwardTo || "- (no forwarding)"],
          ["DLQ forward to", d?.forwardDeadLetteredMessagesTo || "- (local $DeadLetterQueue)"],
          ...(d?.backingQueueName ? [["Backing queue", d.backingQueueName] as [string, string]] : []),
        ];

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap gap-2">
        {(sel.kind === "queue" || sel.kind === "topic") && (
          <Button variant="outline" size="sm" onClick={() => onGoto("send")}>
            <SendIcon /> Send message
          </Button>
        )}
        {sel.kind !== "topic" && (
          <Button variant="outline" size="sm" onClick={() => onGoto("receive")}>
            <InboxIcon /> Receive next
          </Button>
        )}
        {sel.kind === "subscription" && (
          <Button variant="outline" size="sm" onClick={() => onGoto("rules")}>
            <SlidersHorizontalIcon /> Manage rules
          </Button>
        )}
      </div>
      <Card>
        <CardContent className="pt-5">
          <dl className="grid gap-x-8 gap-y-2 text-sm sm:grid-cols-[max-content_1fr]">
            {rows.map(([k, v]) => (
              <div key={k} className="contents">
                <dt className="text-muted-foreground">{k}</dt>
                <dd className="font-mono">{v}</dd>
              </div>
            ))}
          </dl>
        </CardContent>
      </Card>
    </div>
  );
}
