import { useState } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { explorerApi } from "@/lib/api";
import { useStore } from "@/store";
import { TextField } from "./FormControls";

export function DeadLetterDialog({ target, lockToken }: { target: string; lockToken: string }) {
  const store = useStore();
  const [reason, setReason] = useState("");
  const [description, setDescription] = useState("");
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    setBusy(true);
    try {
      await explorerApi.deadletter(store.conn, target, lockToken, reason.trim() || null, description.trim() || null);
      // Remove locally by scanning for the tracked message carrying this token.
      const map = store.locked[target] ?? {};
      const key = Object.keys(map).find((k) => map[k].lockToken === lockToken);
      if (key) store.untrack(target, key);
      toast.success("Dead-lettered");
      store.setDialog(null);
      await store.refresh();
    } catch (e) {
      toast.error("Dead-letter failed: " + (e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <DialogContent>
      <DialogHeader>
        <DialogTitle>Dead-letter message</DialogTitle>
        <DialogDescription>Move this message to the entity's dead-letter queue with an optional reason.</DialogDescription>
      </DialogHeader>
      <div className="space-y-3">
        <TextField label="Reason" value={reason} onChange={setReason} placeholder="e.g. invalid-payload" />
        <TextField label="Description" value={description} onChange={setDescription} placeholder="optional detail" />
      </div>
      <DialogFooter>
        <Button variant="outline" onClick={() => store.setDialog(null)}>Cancel</Button>
        <Button variant="destructive" onClick={() => void submit()} disabled={busy}>Dead-letter</Button>
      </DialogFooter>
    </DialogContent>
  );
}
