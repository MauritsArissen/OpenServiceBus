import { useState } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import {
  DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { HighlightedBodyEditor } from "@/components/variables/HighlightedBodyEditor";
import { VariableChips } from "@/components/variables/VariableChips";
import { VariableLegend } from "@/components/variables/VariableLegend";
import { explorerApi, type CannedMessage } from "@/lib/api";
import { useStore } from "@/store";
import { NumberField, TextField } from "./FormControls";

type PropRow = { key: string; value: string };

const ANY = "*";

/** Full create/edit form for a canned message - every field the Send tab supports plus
 *  name and target scope. Used from the management page; the Send tab's quick
 *  "Save as canned" keeps its lighter name+scope dialog. */
export function CannedEditorDialog({ edit }: { edit?: CannedMessage }) {
  const store = useStore();
  const [name, setName] = useState(edit?.name ?? "");
  const [target, setTarget] = useState(edit?.targetEntity && edit.targetEntity !== ANY ? edit.targetEntity : ANY);
  const [body, setBody] = useState(edit?.body ?? "");
  const [messageId, setMessageId] = useState(edit?.messageId ?? "");
  const [correlationId, setCorrelationId] = useState(edit?.correlationId ?? "");
  const [subject, setSubject] = useState(edit?.subject ?? "");
  const [contentType, setContentType] = useState(edit?.contentType ?? "");
  const [replyTo, setReplyTo] = useState(edit?.replyTo ?? "");
  const [to, setTo] = useState(edit?.to ?? "");
  const [sessionId, setSessionId] = useState(edit?.sessionId ?? "");
  const [partitionKey, setPartitionKey] = useState(edit?.partitionKey ?? "");
  const [ttl, setTtl] = useState(edit?.timeToLiveSeconds ? String(edit.timeToLiveSeconds) : "");
  const [delay, setDelay] = useState(edit?.scheduledDelaySeconds ? String(edit.scheduledDelaySeconds) : "");
  const [props, setProps] = useState<PropRow[]>(
    Object.entries(edit?.properties ?? {}).map(([key, value]) => ({ key, value })),
  );
  const [count, setCount] = useState(String(edit?.count ?? 1));
  const [strategy, setStrategy] = useState(edit?.strategy === "PARALLEL" ? "PARALLEL" : "ATONCE");
  const [busy, setBusy] = useState(false);

  const targets = [
    ...store.queues.map((q) => q.name),
    ...store.topics.map((t) => t.name),
  ];
  if (target !== ANY && !targets.includes(target)) targets.unshift(target);

  const orNull = (v: string) => (v.trim() === "" ? null : v);
  const intOrNull = (v: string) => {
    const p = parseInt(v, 10);
    return Number.isFinite(p) && p > 0 ? p : null;
  };

  const save = async () => {
    const trimmed = name.trim();
    if (trimmed === "") return toast.error("A name is required.");
    setBusy(true);
    try {
      const collected: Record<string, string> = {};
      for (const r of props) {
        const k = r.key.trim();
        if (k) collected[k] = r.value;
      }
      const message: CannedMessage = {
        name: trimmed,
        targetEntity: target === ANY ? ANY : target,
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
        scheduledDelaySeconds: intOrNull(delay),
        properties: Object.keys(collected).length > 0 ? collected : null,
        count: Math.max(1, parseInt(count, 10) || 1),
        strategy,
      };
      if (edit) {
        await explorerApi.updateCanned(edit.name, message);
      } else {
        await explorerApi.createCanned(message);
      }
      toast.success(`Saved '${trimmed}'`);
      store.setDialog(null);
      await store.refreshCanned();
    } catch (e) {
      toast.error("Save failed: " + (e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <DialogContent className="max-h-[90vh] gap-0 overflow-y-auto sm:max-w-2xl">
      <DialogHeader>
        <DialogTitle>{edit ? `Edit '${edit.name}'` : "New canned message"}</DialogTitle>
        <DialogDescription>
          A saved Send form, replayable on any matching entity. Dynamic variables resolve
          at send time.
        </DialogDescription>
      </DialogHeader>
      <div className="mt-4 space-y-4">
        <div className="grid gap-3 sm:grid-cols-2">
          <TextField label="Name" value={name} onChange={setName} placeholder="e.g. order-created" />
          <div className="space-y-1">
            <Label>Available on</Label>
            <Select value={target} onValueChange={setTarget}>
              <SelectTrigger><SelectValue /></SelectTrigger>
              <SelectContent>
                <SelectItem value={ANY}>Any entity</SelectItem>
                {targets.map((t) => (
                  <SelectItem key={t} value={t}>{t}</SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        </div>

        <div className="space-y-1">
          <div className="flex items-center justify-between">
            <Label>Body</Label>
            <VariableLegend />
          </div>
          <HighlightedBodyEditor value={body} onChange={setBody} placeholder='{"orderId": "{{$guid}}"}' env={store.activeEnvValues} />
          <VariableChips
            fields={{
              Body: body, MessageId: messageId, CorrelationId: correlationId, Subject: subject,
              ReplyTo: replyTo, To: to, SessionId: sessionId, PartitionKey: partitionKey,
              ...Object.fromEntries(props.filter((r) => r.key.trim()).map((r) => [`prop ${r.key}`, r.value])),
            }}
            env={store.activeEnvValues}
            className="pt-1"
          />
        </div>

        <div className="grid gap-3 sm:grid-cols-3">
          <TextField label="MessageId" value={messageId} onChange={setMessageId} placeholder="(auto)" mono />
          <TextField label="CorrelationId" value={correlationId} onChange={setCorrelationId} mono />
          <TextField label="Subject" value={subject} onChange={setSubject} />
          <TextField label="ContentType" value={contentType} onChange={setContentType} placeholder="application/json" />
          <TextField label="ReplyTo" value={replyTo} onChange={setReplyTo} />
          <TextField label="To" value={to} onChange={setTo} />
          <TextField label="SessionId" value={sessionId} onChange={setSessionId} />
          <TextField label="PartitionKey" value={partitionKey} onChange={setPartitionKey} />
          <NumberField label="TTL (seconds)" value={ttl} onChange={setTtl} placeholder="∞" min={1} />
          <NumberField label="Schedule delay (seconds)" value={delay} onChange={setDelay} placeholder="send now" min={1} />
          <NumberField label="Copies" value={count} onChange={setCount} min={1} />
          <div className="space-y-1">
            <Label>Strategy</Label>
            <Select value={strategy} onValueChange={setStrategy}>
              <SelectTrigger><SelectValue /></SelectTrigger>
              <SelectContent>
                <SelectItem value="ATONCE">AT ONCE</SelectItem>
                <SelectItem value="PARALLEL">PARALLEL</SelectItem>
              </SelectContent>
            </Select>
          </div>
        </div>

        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <Label>Application properties</Label>
            <Button variant="outline" size="sm" onClick={() => setProps((p) => [...p, { key: "", value: "" }])}>
              Add row
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
              <Button variant="ghost" size="sm" onClick={() => setProps((p) => p.filter((_, j) => j !== i))}>
                ✕
              </Button>
            </div>
          ))}
        </div>
      </div>
      <DialogFooter className="mt-5">
        <Button variant="outline" onClick={() => store.setDialog(null)}>Cancel</Button>
        <Button onClick={() => void save()} disabled={busy}>{busy ? "Saving…" : "Save"}</Button>
      </DialogFooter>
    </DialogContent>
  );
}
