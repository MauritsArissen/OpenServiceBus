import { useState } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { explorerApi } from "@/lib/api";
import { secondsToTimeSpan } from "@/lib/format";
import { useStore } from "@/store";
import { NumberField, SwitchField, TextField } from "./FormControls";

export function CreateSubscriptionDialog({ presetTopic }: { presetTopic?: string }) {
  const store = useStore();
  const [topic, setTopic] = useState(presetTopic ?? store.topics[0]?.name ?? "");
  const [name, setName] = useState("");
  const [maxDelivery, setMaxDelivery] = useState("10");
  const [lockDuration, setLockDuration] = useState("60");
  const [ttl, setTtl] = useState("");
  const [dleOnExpiration, setDleOnExpiration] = useState(false);
  const [requiresSession, setRequiresSession] = useState(false);
  const [forwardTo, setForwardTo] = useState("");
  const [forwardDlq, setForwardDlq] = useState("");
  const [busy, setBusy] = useState(false);

  const create = async () => {
    if (topic === "") return toast.error("Select a topic.");
    if (name.trim() === "") return toast.error("Subscription name is required.");
    setBusy(true);
    try {
      const options: Record<string, unknown> = {
        maxDeliveryCount: parseInt(maxDelivery, 10) || 10,
        lockDuration: secondsToTimeSpan(parseInt(lockDuration, 10) || 60),
        deadLetteringOnMessageExpiration: dleOnExpiration,
        requiresSession,
      };
      const ttlN = parseInt(ttl, 10);
      if (ttlN > 0) options.defaultMessageTimeToLive = secondsToTimeSpan(ttlN);
      if (forwardTo.trim()) options.forwardTo = forwardTo.trim();
      if (forwardDlq.trim()) options.forwardDeadLetteredMessagesTo = forwardDlq.trim();

      await explorerApi.createSubscription(store.mgmt, topic, name.trim(), options);
      toast.success(`Created subscription '${topic}/${name.trim()}'`);
      store.setDialog(null);
      await store.refresh();
      store.select({ kind: "subscription", name: topic, sub: name.trim() });
    } catch (e) {
      toast.error("Create failed: " + (e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <DialogContent>
      <DialogHeader>
        <DialogTitle>Create subscription</DialogTitle>
        <DialogDescription>A $Default TrueFilter rule is installed automatically.</DialogDescription>
      </DialogHeader>
      <div className="space-y-3">
        <div className="space-y-1">
          <Label>Topic</Label>
          <Select value={topic} onValueChange={setTopic}>
            <SelectTrigger><SelectValue placeholder="Select a topic" /></SelectTrigger>
            <SelectContent>
              {store.topics.map((t) => (
                <SelectItem key={t.name} value={t.name}>{t.name}</SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <TextField label="Subscription name" value={name} onChange={setName} mono placeholder="all-events" />
        <div className="grid grid-cols-2 gap-3">
          <NumberField label="Max delivery count" value={maxDelivery} onChange={setMaxDelivery} min={1} />
          <NumberField label="Lock duration (seconds)" value={lockDuration} onChange={setLockDuration} min={1} />
          <NumberField label="Default TTL (seconds)" value={ttl} onChange={setTtl} placeholder="∞" min={1} />
        </div>
        <div className="grid grid-cols-2 gap-3">
          <SwitchField label="DLQ on TTL expiry" checked={dleOnExpiration} onChange={setDleOnExpiration} />
          <SwitchField label="Sessions required" checked={requiresSession} onChange={setRequiresSession} />
        </div>
        <div className="grid grid-cols-2 gap-3">
          <TextField label="Forward to" value={forwardTo} onChange={setForwardTo} mono />
          <TextField label="Forward dead-lettered to" value={forwardDlq} onChange={setForwardDlq} mono />
        </div>
      </div>
      <DialogFooter>
        <Button variant="outline" onClick={() => store.setDialog(null)}>Cancel</Button>
        <Button onClick={() => void create()} disabled={busy}>Create subscription</Button>
      </DialogFooter>
    </DialogContent>
  );
}
