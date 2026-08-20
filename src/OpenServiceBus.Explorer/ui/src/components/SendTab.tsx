import { ChevronRightIcon, PlusIcon, SendIcon, XIcon } from "lucide-react";
import {
  useEffect,
  useImperativeHandle,
  useState,
  type RefObject,
} from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Collapsible } from "@/components/ui/collapsible";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { explorerApi, type CannedMessage, type SendPayload } from "@/lib/api";
import { formatTime, type Selected } from "@/lib/format";
import { cn } from "@/lib/utils";
import { entityAddress, useStore } from "@/store";
import { Combobox, ComboboxContent, ComboboxInput } from "./ui/combobox";

type PropRow = { key: string; value: string };

export interface SendTabRef {
  getSendPayload: () => SendPayload;
}

export function SendTab({
  ref,
  sel,
  kind,
}: {
  ref?: RefObject<SendTabRef | null>;
  sel: Selected;
  kind: "send" | "cannedMessage";
}) {
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
  const [cannedMessages, setCannedMessages] = useState<CannedMessage[]>([]);
  const [selectedCannedMessageName, setSelectedCannedMessageName] =
    useState<string>("");

  const isSendMode = kind === "send";

  const n = Math.max(1, parseInt(count, 10) || 1);
  const orNull = (v: string) => (v.trim() === "" ? null : v);
  const intOrNull = (v: string) => {
    const p = parseInt(v, 10);
    return Number.isFinite(p) && p > 0 ? p : null;
  };

  const load = async () => {
    if (isSendMode) {
      try {
        setCannedMessages(
          await explorerApi.listCannedMessages(store.mgmt, sel.name),
        );
      } catch (e) {
        toast.error("Loading canned messages failed: " + (e as Error).message);
      }
    }
  };

  useEffect(() => {
    void load();
    // reload when the dialog closes (a rule may have been added/edited)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sel.name, store.dialog]);

  useEffect(() => {
    if (isSendMode && selectedCannedMessageName) {
      const m = cannedMessages.find(
        (m) => m.name === selectedCannedMessageName,
      );
      if (m) {
        setMessageId(m.message.messageId ?? "");
        setCorrelationId(m.message.correlationId ?? "");
        setSubject(m.message.subject ?? "");
        setBody(m.message.body ?? "");
        setContentType(m.message.contentType ?? "");
        setReplyTo(m.message.replyTo ?? "");
        setTo(m.message.to ?? "");
        setSessionId(m.message.sessionId ?? "");
        setPartitionKey(m.message.partitionKey ?? "");
        setTtl(
          m.message.timeToLiveSeconds !== null
            ? m.message.timeToLiveSeconds.toString()
            : "",
        );
        setScheduledSecs(
          m.message.scheduledEnqueueTime !== null
            ? Math.max(
                0,
                Math.floor(
                  (new Date(m.message.scheduledEnqueueTime).getTime() -
                    Date.now()) /
                    1000,
                ),
              ).toString()
            : "",
        );
        const props: PropRow[] = [];
        if (m.message.properties) {
          for (const [k, v] of Object.entries(m.message.properties)) {
            props.push({ key: k, value: v });
          }
        }
        setProps(props);
      }
    }
  }, [kind, selectedCannedMessageName, cannedMessages]);

  const getPayload = (): SendPayload => {
    const schedSecs = intOrNull(scheduledSecs);

    const collected: Record<string, string> = {};
    for (const r of props) {
      const k = r.key.trim();
      if (k) collected[k] = r.value;
    }

    return {
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
      scheduledEnqueueTime: schedSecs
        ? new Date(Date.now() + schedSecs * 1000).toISOString()
        : null,
      properties: Object.keys(collected).length > 0 ? collected : null,
      count: n,
      strategy,
    };
  };

  const send = async () => {
    setSending(true);
    try {
      const result = await explorerApi.send(getPayload());
      if (result.scheduledFor) {
        toast.success(
          `Scheduled ${result.sentCount} for ${formatTime(result.scheduledFor)} (${result.strategy})`,
        );
      } else if (result.sentCount === 1) {
        toast.success(`Sent to ${target} (id=${result.messageId ?? "auto"})`);
      } else {
        toast.success(
          `Sent ${result.sentCount} to ${target} (${result.strategy})`,
        );
      }
      await store.refresh();
    } catch (e) {
      toast.error("Send failed: " + (e as Error).message);
    } finally {
      setSending(false);
    }
  };

  useImperativeHandle(ref, () => ({
    getSendPayload() {
      return getPayload();
    },
  }));

  return (
    <Card>
      <CardContent className="flex flex-col space-y-4 pt-5">
        {isSendMode && cannedMessages?.length > 0 && (
          <div className="flex flex-row justify-end-safe items-center gap-2">
            <span>Canned messages:</span>
            <Select
              value={selectedCannedMessageName}
              onValueChange={setSelectedCannedMessageName}
            >
              <SelectTrigger className="w-36">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {cannedMessages.map((m) => (
                  <SelectItem key={m.name} value={m.name}>
                    {m.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        )}
        <div className="grid gap-3 sm:grid-cols-3">
          <Field label="MessageId">
            <Input
              value={messageId}
              onChange={(e) => setMessageId(e.target.value)}
              placeholder="(auto-generated)"
            />
          </Field>
          <Field label="CorrelationId">
            <Input
              value={correlationId}
              onChange={(e) => setCorrelationId(e.target.value)}
            />
          </Field>
          <Field label="Subject">
            <Input
              value={subject}
              onChange={(e) => setSubject(e.target.value)}
            />
          </Field>
        </div>

        <Field label="Body">
          <Textarea
            rows={7}
            value={body}
            onChange={(e) => setBody(e.target.value)}
            className="font-mono text-xs"
          />
        </Field>

        <Collapsible
          open={advancedOpen}
          onOpenChange={setAdvancedOpen}
          trigger={
            <span className="flex items-center gap-1 text-sm font-medium text-muted-foreground">
              <ChevronRightIcon
                className={cn(
                  "size-4 transition-transform",
                  advancedOpen && "rotate-90",
                )}
              />
              Advanced
            </span>
          }
        >
          <div className="mt-3 grid gap-3 sm:grid-cols-3">
            <Field label="ContentType">
              {/* <Input
                value={contentType}
                onChange={(e) => setContentType(e.target.value)}
                placeholder="application/json"
              /> */}
              <Combobox
                options={[
                  "application/json",
                  "application/xml",
                  "application/cloudevents+json",
                ]}
                value={contentType}
                onValueChange={setContentType}
              >
                <ComboboxInput placeholder="application/json" />
                <ComboboxContent />
              </Combobox>
            </Field>
            <Field label="ReplyTo">
              <Input
                value={replyTo}
                onChange={(e) => setReplyTo(e.target.value)}
              />
            </Field>
            <Field label="To">
              <Input value={to} onChange={(e) => setTo(e.target.value)} />
            </Field>
            <Field label="SessionId">
              <Input
                value={sessionId}
                onChange={(e) => setSessionId(e.target.value)}
                placeholder="(only for session entities)"
              />
            </Field>
            <Field label="PartitionKey">
              <Input
                value={partitionKey}
                onChange={(e) => setPartitionKey(e.target.value)}
              />
            </Field>
            <Field label="TimeToLive (seconds)">
              <Input
                type="number"
                min={1}
                value={ttl}
                onChange={(e) => setTtl(e.target.value)}
                placeholder="∞"
              />
            </Field>
            <Field label="Scheduled for (seconds from now)">
              <Input
                type="number"
                min={1}
                value={scheduledSecs}
                onChange={(e) => setScheduledSecs(e.target.value)}
                placeholder="now"
              />
            </Field>
          </div>
        </Collapsible>

        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <Label>Application properties</Label>
            <Button
              variant="outline"
              size="sm"
              onClick={() => setProps((p) => [...p, { key: "", value: "" }])}
            >
              <PlusIcon /> Add row
            </Button>
          </div>
          {props.map((row, i) => (
            <div key={i} className="flex gap-2">
              <Input
                value={row.key}
                placeholder="key"
                onChange={(e) =>
                  setProps((p) =>
                    p.map((r, j) =>
                      j === i ? { ...r, key: e.target.value } : r,
                    ),
                  )
                }
                className="font-mono text-xs"
              />
              <Input
                value={row.value}
                placeholder="value"
                onChange={(e) =>
                  setProps((p) =>
                    p.map((r, j) =>
                      j === i ? { ...r, value: e.target.value } : r,
                    ),
                  )
                }
                className="font-mono text-xs"
              />
              <Button
                variant="ghost"
                size="icon"
                onClick={() => setProps((p) => p.filter((_, j) => j !== i))}
              >
                <XIcon />
              </Button>
            </div>
          ))}
        </div>

        {kind === "send" && (
          <div className="flex flex-wrap items-end gap-3 border-t pt-4">
            <Field label="Copies">
              <Input
                type="number"
                min={1}
                value={count}
                onChange={(e) => setCount(e.target.value)}
                className="w-24"
              />
            </Field>
            <Field label="Strategy">
              <Select value={strategy} onValueChange={setStrategy}>
                <SelectTrigger className="w-36">
                  <SelectValue />
                </SelectTrigger>
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
              {n > 1 &&
                messageId.trim() !== "" &&
                `MessageId will be suffixed -0…-${n - 1}. `}
              {sel.kind === "topic"
                ? "Sending to a topic - the broker evaluates each subscription's rules and fans out."
                : "Sent via Azure.Messaging.ServiceBus SDK."}
            </p>
          </div>
        )}
      </CardContent>
    </Card>
  );
}

function Field({
  label,
  children,
}: {
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div className="space-y-1">
      <Label>{label}</Label>
      {children}
    </div>
  );
}
