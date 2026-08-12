import { useState } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { explorerApi } from "@/lib/api";
import { useStore } from "@/store";
import { SwitchField } from "./FormControls";

const deriveDestination = (dlqAddress: string): string | null => {
  const stripped = dlqAddress.endsWith("/$Transfer/$DeadLetterQueue")
    ? dlqAddress.slice(0, -"/$Transfer/$DeadLetterQueue".length)
    : dlqAddress.endsWith("/$DeadLetterQueue")
      ? dlqAddress.slice(0, -"/$DeadLetterQueue".length)
      : null;
  if (stripped == null) return null;
  const idx = stripped.indexOf("/Subscriptions/");
  return idx > 0 ? stripped.slice(0, idx) : stripped;
};

export function ResendDialog({ target, sequenceNumbers }: { target: string; sequenceNumbers: number[] }) {
  const store = useStore();
  const derived = deriveDestination(target);
  const [destination, setDestination] = useState(derived ?? "");
  const [keepMessageId, setKeepMessageId] = useState(false);
  const [busy, setBusy] = useState(false);
  const many = sequenceNumbers.length > 1;

  const options = Array.from(
    new Set([...(derived ? [derived] : []), ...store.queues.map((q) => q.name), ...store.topics.map((t) => t.name)]),
  );

  const submit = async () => {
    setBusy(true);
    try {
      const r = await explorerApi.resend(store.conn, target, sequenceNumbers, destination || null, keepMessageId);
      if (r.failed === 0) {
        toast.success(
          many ? `Resent ${r.succeeded}/${r.total} to ${r.destination}` : `Resent to ${r.destination}`,
        );
      } else {
        const firstError = r.results.find((x) => !x.ok)?.error ?? "unknown error";
        toast.warning(`Resend: ${r.succeeded} succeeded, ${r.failed} failed (${firstError})`);
      }
      store.setDialog(null);
      await store.refresh();
    } catch (e) {
      toast.error("Resend failed: " + (e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <DialogContent>
      <DialogHeader>
        <DialogTitle>{many ? `Resend ${sequenceNumbers.length} messages` : "Resend message"}</DialogTitle>
        <DialogDescription>
          Sends a brand-new copy with the same body and properties but fresh broker metadata.
          The original{many ? "s stay" : " stays"} in the dead-letter queue.
        </DialogDescription>
      </DialogHeader>
      <div className="space-y-3">
        <div className="space-y-1">
          <Label>Destination</Label>
          <Select value={destination} onValueChange={setDestination}>
            <SelectTrigger className="w-full font-mono text-xs"><SelectValue placeholder="Pick an entity" /></SelectTrigger>
            <SelectContent>
              {options.map((name) => (
                <SelectItem key={name} value={name} className="font-mono text-xs">
                  {name}{name === derived ? "  (source)" : ""}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <SwitchField label="Keep original MessageId" checked={keepMessageId} onChange={setKeepMessageId} />
        {keepMessageId && (
          <p className="text-xs text-muted-foreground">
            On duplicate-detection entities the broker silently drops a kept MessageId that is
            still inside the dedup window - the resend reports success but nothing arrives.
          </p>
        )}
      </div>
      <DialogFooter>
        <Button variant="outline" onClick={() => store.setDialog(null)}>Cancel</Button>
        <Button onClick={() => void submit()} disabled={busy || destination === ""}>
          {many ? `Resend ${sequenceNumbers.length}` : "Resend"}
        </Button>
      </DialogFooter>
    </DialogContent>
  );
}
