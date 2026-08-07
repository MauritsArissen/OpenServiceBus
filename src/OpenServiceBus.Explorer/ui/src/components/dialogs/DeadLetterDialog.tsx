import { useState } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { explorerApi } from "@/lib/api";
import { useStore } from "@/store";
import { TextField } from "./FormControls";

export function DeadLetterDialog({ target, lockTokens }: { target: string; lockTokens: string[] }) {
  const store = useStore();
  const [reason, setReason] = useState("");
  const [description, setDescription] = useState("");
  const [busy, setBusy] = useState(false);
  const many = lockTokens.length > 1;

  const submit = async () => {
    setBusy(true);
    try {
      const r = await explorerApi.bulk(
        store.conn, target, "deadletter", lockTokens, reason.trim() || null, description.trim() || null,
      );
      const gone = new Set(r.results.filter((x) => x.ok || x.lockLost).map((x) => x.lockToken));
      const map = store.locked[target] ?? {};
      store.untrackMany(target, Object.keys(map).filter((k) => gone.has(map[k].lockToken ?? "")));
      if (r.failed === 0) {
        toast.success(many ? `Dead-lettered ${r.succeeded}/${r.total}` : "Dead-lettered");
      } else {
        const firstError = r.results.find((x) => !x.ok)?.error ?? "unknown error";
        toast.warning(`Dead-letter: ${r.succeeded} succeeded, ${r.failed} failed (${firstError})`);
      }
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
        <DialogTitle>{many ? `Dead-letter ${lockTokens.length} messages` : "Dead-letter message"}</DialogTitle>
        <DialogDescription>
          {many
            ? "Move the selected messages to the entity's dead-letter queue with an optional shared reason."
            : "Move this message to the entity's dead-letter queue with an optional reason."}
        </DialogDescription>
      </DialogHeader>
      <div className="space-y-3">
        <TextField label="Reason" value={reason} onChange={setReason} placeholder="e.g. invalid-payload" />
        <TextField label="Description" value={description} onChange={setDescription} placeholder="optional detail" />
      </div>
      <DialogFooter>
        <Button variant="outline" onClick={() => store.setDialog(null)}>Cancel</Button>
        <Button variant="destructive" onClick={() => void submit()} disabled={busy}>
          {many ? `Dead-letter ${lockTokens.length}` : "Dead-letter"}
        </Button>
      </DialogFooter>
    </DialogContent>
  );
}
