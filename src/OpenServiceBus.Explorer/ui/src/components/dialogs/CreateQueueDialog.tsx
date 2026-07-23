import { useState } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import {
  DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from "@/components/ui/dialog";
import { explorerApi } from "@/lib/api";
import { secondsToTimeSpan } from "@/lib/format";
import { useStore } from "@/store";
import { NumberField, SwitchField, TextField } from "./FormControls";

export function CreateQueueDialog() {
  const store = useStore();
  const [name, setName] = useState("");
  const [maxDelivery, setMaxDelivery] = useState("10");
  const [lockDuration, setLockDuration] = useState("60");
  const [ttl, setTtl] = useState("");
  const [dleOnExpiration, setDleOnExpiration] = useState(false);
  const [requiresSession, setRequiresSession] = useState(false);
  const [requiresDedup, setRequiresDedup] = useState(false);
  const [dedupWindow, setDedupWindow] = useState("");
  const [forwardTo, setForwardTo] = useState("");
  const [forwardDlq, setForwardDlq] = useState("");
  const [busy, setBusy] = useState(false);

  const create = async () => {
    if (name.trim() === "") return toast.error("Queue name is required.");
    setBusy(true);
    try {
      const options: Record<string, unknown> = {
        maxDeliveryCount: parseInt(maxDelivery, 10) || 10,
        lockDuration: secondsToTimeSpan(parseInt(lockDuration, 10) || 60),
        deadLetteringOnMessageExpiration: dleOnExpiration,
        requiresSession,
        requiresDuplicateDetection: requiresDedup,
      };
      const ttlN = parseInt(ttl, 10);
      if (ttlN > 0) options.defaultMessageTimeToLive = secondsToTimeSpan(ttlN);
      const dwN = parseInt(dedupWindow, 10);
      if (requiresDedup && dwN > 0) options.duplicateDetectionHistoryTimeWindow = secondsToTimeSpan(dwN);
      if (forwardTo.trim()) options.forwardTo = forwardTo.trim();
      if (forwardDlq.trim()) options.forwardDeadLetteredMessagesTo = forwardDlq.trim();

      await explorerApi.createQueue(store.mgmt, name.trim(), options);
      toast.success(`Created queue '${name.trim()}'`);
      store.setDialog(null);
      await store.refresh();
      store.select({ kind: "queue", name: name.trim() });
    } catch (e) {
      toast.error("Create failed: " + (e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <DialogContent>
      <DialogHeader>
        <DialogTitle>Create queue</DialogTitle>
        <DialogDescription>A matching $DeadLetterQueue is created automatically.</DialogDescription>
      </DialogHeader>
      <div className="space-y-3">
        <TextField label="Queue name" value={name} onChange={setName} mono placeholder="orders" />
        <div className="grid grid-cols-2 gap-3">
          <NumberField label="Max delivery count" value={maxDelivery} onChange={setMaxDelivery} min={1} />
          <NumberField label="Lock duration (seconds)" value={lockDuration} onChange={setLockDuration} min={1} />
          <NumberField label="Default TTL (seconds)" value={ttl} onChange={setTtl} placeholder="∞" min={1} />
          <NumberField label="Dedup window (seconds)" value={dedupWindow} onChange={setDedupWindow} placeholder="600 (10 min)" min={1} />
        </div>
        <div className="grid grid-cols-2 gap-3">
          <SwitchField label="DLQ on TTL expiry" checked={dleOnExpiration} onChange={setDleOnExpiration} />
          <SwitchField label="Sessions required" checked={requiresSession} onChange={setRequiresSession} />
          <SwitchField label="Duplicate detection" checked={requiresDedup} onChange={setRequiresDedup} />
        </div>
        <div className="grid grid-cols-2 gap-3">
          <TextField label="Forward to" value={forwardTo} onChange={setForwardTo} mono />
          <TextField label="Forward dead-lettered to" value={forwardDlq} onChange={setForwardDlq} mono />
        </div>
      </div>
      <DialogFooter>
        <Button variant="outline" onClick={() => store.setDialog(null)}>Cancel</Button>
        <Button onClick={() => void create()} disabled={busy}>Create queue</Button>
      </DialogFooter>
    </DialogContent>
  );
}
