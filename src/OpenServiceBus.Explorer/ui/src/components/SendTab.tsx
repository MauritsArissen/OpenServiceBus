import { BookmarkIcon, ChevronRightIcon, PlusIcon, SendIcon, XIcon } from "lucide-react";
import { useState } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Collapsible } from "@/components/ui/collapsible";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { explorerApi, type CannedMessage } from "@/lib/api";
import { formatTime, type Selected } from "@/lib/format";
import { cn } from "@/lib/utils";
import { HighlightedBodyEditor } from "@/components/variables/HighlightedBodyEditor";
import { VariableChips } from "@/components/variables/VariableChips";
import { VariableLegend } from "@/components/variables/VariableLegend";
import { entityAddress, useStore } from "@/store";

type PropRow = { key: string; value: string };

export function SendTab({ sel }: { sel: Selected }) {
  const store = useStore();
  const target = entityAddress(sel);

  const [messageId, setMessageId] = useState("");
  const [correlationId, setCorrelationId] = useState("");
  const [subject, setSubject] = useState("");
  const [body, setBody] = useState("");
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [contentType, setContentType] = useState("");
  const [replyTo, setReplyTo] = useState("");
  const [to, setTo] = useState("");
  const [sessionId, setSessionId] = useState("");
  const [partitionKey, setPartitionKey] = useState("");
  const [ttl, setTtl] = useState("");
  const [scheduledSecs, setScheduledSecs] = useState("");
  const [props, setProps] = useState<PropRow[]>([]);
  const [count, setCount] = useState("1");
  const [strategy, setStrategy] = useState("ATONCE");
  const [sending, setSending] = useState(false);

  const n = Math.max(1, parseInt(count, 10) || 1);
  const orNull = (v: string) => (v.trim() === "" ? null : v);
  const intOrNull = (v: string) => {
    const p = parseInt(v, 10);
    return Number.isFinite(p) && p > 0 ? p : null;
  };

  const availableCanned = store.canned.filter(
    (m) => !m.targetEntity || m.targetEntity === "*" || m.targetEntity === target,
  );

  const applyCanned = (m: CannedMessage) => {
    setMessageId(m.messageId ?? "");
    setCorrelationId(m.correlationId ?? "");
    setSubject(m.subject ?? "");
    setBody(m.body ?? "");
    setContentType(m.contentType ?? "");
    setReplyTo(m.replyTo ?? "");
    setTo(m.to ?? "");
    setSessionId(m.sessionId ?? "");
    setPartitionKey(m.partitionKey ?? "");
    setTtl(m.timeToLiveSeconds ? String(m.timeToLiveSeconds) : "");
    setScheduledSecs(m.scheduledDelaySeconds ? String(m.scheduledDelaySeconds) : "");
    setProps(Object.entries(m.properties ?? {}).map(([key, value]) => ({ key, value })));
    setCount(String(m.count ?? 1));
    setStrategy(m.strategy === "PARALLEL" ? "PARALLEL" : "ATONCE");
    toast.success(`Loaded '${m.name}'`);
  };

  const saveAsCanned = () => {
    const collected: Record<string, string> = {};
    for (const r of props) {
      const k = r.key.trim();
      if (k) collected[k] = r.value;
    }
    store.setDialog({
      type: "saveCanned",
      draft: {
        name: "",
        targetEntity: target,
        body: orNull(body),
        messageId: orNull(messageId),
        correlationId: orNull(correlationId),
        subject: orNull(subject),
        contentType: orNull(contentType),
        replyTo: orNull(replyTo),
        to: orNull(to),
        sessionId: orNull(sessionId),
        partitionKey: orNull(partitionKey),
        timeToLiveSeconds: intOrNull(ttl),
        scheduledDelaySeconds: intOrNull(scheduledSecs),
        properties: Object.keys(collected).length > 0 ? collected : null,
        count: n,
        strategy,
      },
    });
  };

  const send = async () => {
    setSending(true);
    try {
      const collected: Record<string, string> = {};
      for (const r of props) {
        const k = r.key.trim();
        if (k) collected[k] = r.value;
      }
      const schedSecs = intOrNull(scheduledSecs);
      const result = await explorerApi.send({
        connectionString: store.conn,
        queue: target,
        body: orNull(body),
        messageId: orNull(messageId),
        correlationId: orNull(correlationId),
        subject: orNull(subject),
        contentType: orNull(contentType),
        replyTo: orNull(replyTo),
        to: orNull(to),
        sessionId: orNull(sessionId),
        partitionKey: orNull(partitionKey),
        timeToLiveSeconds: intOrNull(ttl),
        scheduledEnqueueTime: schedSecs ? new Date(Date.now() + schedSecs * 1000).toISOString() : null,
        properties: Object.keys(collected).length > 0 ? collected : null,
        count: n,
        strategy,
      });
      if (result.scheduledFor) {
        toast.success(`Scheduled ${result.sentCount} for ${formatTime(result.scheduledFor)} (${result.strategy})`);
      } else if (result.sentCount === 1) {
        toast.success(`Sent to ${target} (id=${result.messageId ?? "auto"})`);
      } else {
        toast.success(`Sent ${result.sentCount} to ${target} (${result.strategy})`);
      }
      await store.refresh();
    } catch (e) {
      toast.error("Send failed: " + (e as Error).message);
    } finally {
      setSending(false);
    }
  };

  return (
    <Card>
      <CardContent className="space-y-4 pt-5">
        <div className="flex flex-col gap-2 rounded-md border bg-muted/40 p-2.5 sm:flex-row sm:items-center">
          <div className="flex min-w-0 flex-1 items-center gap-2">
            <BookmarkIcon className="size-4 shrink-0 text-muted-foreground" />
            <Select
              value=""
              onValueChange={(name) => {
                const m = availableCanned.find((c) => c.name === name);
                if (m) applyCanned(m);
              }}
            >
              <SelectTrigger className="h-8 min-w-0 flex-1 sm:max-w-72">
                <SelectValue
                  placeholder={
                    availableCanned.length === 0
                      ? "No canned messages for this entity"
                      : `Load a canned message (${availableCanned.length})`
                  }
                />
              </SelectTrigger>
              <SelectContent>
                {availableCanned.map((m) => (
                  <SelectItem key={m.name} value={m.name}>
                    <span className="font-mono text-xs">{m.name}</span>
                    {(!m.targetEntity || m.targetEntity === "*") && (
                      <span className="ml-1.5 text-[10px] text-muted-foreground">any</span>
                    )}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <div className="flex gap-2">
            <Button variant="outline" size="sm" className="flex-1 sm:flex-none" onClick={saveAsCanned}>
              Save as canned
            </Button>
            <Button variant="ghost" size="sm" className="flex-1 sm:flex-none" onClick={() => store.setView("canned")}>
              Manage
            </Button>
          </div>
        </div>

        <div className="grid gap-3 sm:grid-cols-3">
          <Field label="MessageId">
            <Input value={messageId} onChange={(e) => setMessageId(e.target.value)} placeholder="(auto-generated)" />
          </Field>
          <Field label="CorrelationId">
            <Input value={correlationId} onChange={(e) => setCorrelationId(e.target.value)} />
          </Field>
          <Field label="Subject">
            <Input value={subject} onChange={(e) => setSubject(e.target.value)} />
          </Field>
        </div>

        <div className="space-y-1">
          <div className="flex items-center justify-between">
            <Label>Body</Label>
            <VariableLegend />
          </div>
          <HighlightedBodyEditor value={body} onChange={setBody} placeholder='{"orderId": "{{$guid}}"}' />
          <VariableChips
            fields={{
              Body: body, MessageId: messageId, CorrelationId: correlationId, Subject: subject,
              ReplyTo: replyTo, To: to, SessionId: sessionId, PartitionKey: partitionKey,
              ...Object.fromEntries(props.filter((r) => r.key.trim()).map((r) => [`prop ${r.key}`, r.value])),
            }}
            className="pt-1"
          />
        </div>

        <Collapsible
          open={advancedOpen}
          onOpenChange={setAdvancedOpen}
          trigger={
            <span className="flex items-center gap-1 text-sm font-medium text-muted-foreground">
              <ChevronRightIcon className={cn("size-4 transition-transform", advancedOpen && "rotate-90")} />
              Advanced
            </span>
          }
        >
          <div className="mt-3 grid gap-3 sm:grid-cols-3">
            <Field label="ContentType">
              <Input value={contentType} onChange={(e) => setContentType(e.target.value)} placeholder="application/json" />
            </Field>
            <Field label="ReplyTo">
              <Input value={replyTo} onChange={(e) => setReplyTo(e.target.value)} />
            </Field>
            <Field label="To">
              <Input value={to} onChange={(e) => setTo(e.target.value)} />
            </Field>
            <Field label="SessionId">
              <Input value={sessionId} onChange={(e) => setSessionId(e.target.value)} placeholder="(only for session entities)" />
            </Field>
            <Field label="PartitionKey">
              <Input value={partitionKey} onChange={(e) => setPartitionKey(e.target.value)} />
            </Field>
            <Field label="TimeToLive (seconds)">
              <Input type="number" min={1} value={ttl} onChange={(e) => setTtl(e.target.value)} placeholder="∞" />
            </Field>
            <Field label="Scheduled for (seconds from now)">
              <Input type="number" min={1} value={scheduledSecs} onChange={(e) => setScheduledSecs(e.target.value)} placeholder="now" />
            </Field>
          </div>
        </Collapsible>

        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <Label>Application properties</Label>
            <Button variant="outline" size="sm" onClick={() => setProps((p) => [...p, { key: "", value: "" }])}>
              <PlusIcon /> Add row
            </Button>
          </div>
          {props.map((row, i) => (
            <div key={i} className="flex gap-2">
              <Input
                value={row.key} placeholder="key"
                onChange={(e) => setProps((p) => p.map((r, j) => (j === i ? { ...r, key: e.target.value } : r)))}
                className="font-mono text-xs"
              />
              <Input
                value={row.value} placeholder="value"
                onChange={(e) => setProps((p) => p.map((r, j) => (j === i ? { ...r, value: e.target.value } : r)))}
                className="font-mono text-xs"
              />
              <Button variant="ghost" size="icon" onClick={() => setProps((p) => p.filter((_, j) => j !== i))}>
                <XIcon />
              </Button>
            </div>
          ))}
        </div>

        <div className="flex flex-wrap items-end gap-3 border-t pt-4">
          <Field label="Copies">
            <Input type="number" min={1} value={count} onChange={(e) => setCount(e.target.value)} className="w-24" />
          </Field>
          <Field label="Strategy">
            <Select value={strategy} onValueChange={setStrategy}>
              <SelectTrigger className="w-36"><SelectValue /></SelectTrigger>
              <SelectContent>
                <SelectItem value="ATONCE">AT ONCE</SelectItem>
                <SelectItem value="PARALLEL">PARALLEL</SelectItem>
              </SelectContent>
            </Select>
          </Field>
          <Button onClick={() => void send()} disabled={sending}>
            <SendIcon /> {sending ? "Sending…" : n > 1 ? `Send ${n}` : "Send"}
          </Button>
          <p className="basis-full text-xs text-muted-foreground">
            {n > 1 &&
              (strategy === "PARALLEL"
                ? `PARALLEL: ${n} concurrent SendMessageAsync calls, each its own AMQP transfer. `
                : `AT ONCE: a single SendMessagesAsync batch of ${n}. `)}
            {n > 1 && messageId.trim() !== "" && `MessageId will be suffixed -0…-${n - 1}. `}
            {sel.kind === "topic"
              ? "Sending to a topic - the broker evaluates each subscription's rules and fans out."
              : "Sent via Azure.Messaging.ServiceBus SDK."}
          </p>
        </div>
      </CardContent>
    </Card>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="space-y-1">
      <Label>{label}</Label>
      {children}
    </div>
  );
}
